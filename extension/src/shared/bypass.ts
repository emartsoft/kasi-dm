// Bypass key (KASI-BRW-003). downloads.onCreated cannot see the keyboard, so the content script reports modifier-held clicks
// and the background decides here (plan 2.4). Only a hash of the link is stored.
import type { Rules } from '../types/protocol';
import { hashUrl } from './hash';
import { bypassHeld } from './rules-match';
import type { StorageAreaLike } from './rules-cache';

export const MODIFIER_KIND = 'kasi-modifier';
export const GRACE_MS = 100;
export const LINK_FRESH_MS = 15_000;
export const OTHER_FRESH_MS = 3_000;
const KEY = 'modifier';

export interface ModifierMessage {
  kind: typeof MODIFIER_KIND;
  mods: { alt: boolean; ctrl: boolean; shift: boolean };
  href?: string;
}

export function parseModifierMessage(value: unknown): ModifierMessage | undefined {
  if (typeof value !== 'object' || value === null) return undefined;
  const { kind, mods, href } = value as Record<string, unknown>;
  if (kind !== MODIFIER_KIND || typeof mods !== 'object' || mods === null) return undefined;
  const { alt, ctrl, shift } = mods as Record<string, unknown>;
  if (typeof alt !== 'boolean' || typeof ctrl !== 'boolean' || typeof shift !== 'boolean') {
    return undefined;
  }
  if (href !== undefined && (typeof href !== 'string' || href.length > 8192)) return undefined;
  return href === undefined
    ? { kind, mods: { alt, ctrl, shift } }
    : { kind, mods: { alt, ctrl, shift }, href };
}

interface ModifierRecord {
  at: number;
  alt: boolean;
  ctrl: boolean;
  shift: boolean;
  tabId?: number;
  hrefHash?: string;
}

export type UrlHashes = () => Promise<string[]>;

export interface BypassOptions {
  area: StorageAreaLike;
  now?: () => number;
  sleep?: (ms: number) => Promise<void>;
}

export function createBypass(options: BypassOptions) {
  const now = options.now ?? Date.now;
  const sleep = options.sleep ?? ((ms) => new Promise<void>((done) => setTimeout(done, ms)));
  let last: ModifierRecord | undefined;

  async function current(): Promise<ModifierRecord | undefined> {
    if (last) return last;
    const stored = (await options.area.get(KEY))[KEY];
    if (typeof stored === 'object' && stored !== null && 'at' in stored) {
      last = stored as ModifierRecord;
    }
    return last;
  }

  // `urlHashes`: hashes of the download's URL, final URL and the redirect hops that led to it. Asked on every look because
  // Firefox can report a hop (webRequest) a few ms after the download was created.
  async function held(key: Rules['bypassKey'], urlHashes: UrlHashes): Promise<boolean> {
    const record = await current();
    if (!record || !bypassHeld(key, record)) return false;
    const fromLink = record.hrefHash !== undefined && (await urlHashes()).includes(record.hrefHash);
    return now() - record.at <= (fromLink ? LINK_FRESH_MS : OTHER_FRESH_MS);
  }

  return {
    record: async (message: ModifierMessage, tabId?: number): Promise<void> => {
      const record: ModifierRecord = { at: now(), ...message.mods };
      if (tabId !== undefined) record.tabId = tabId;
      if (message.href) record.hrefHash = await hashUrl(message.href);
      last = record;
      await options.area.set({ [KEY]: record });
    },
    // The message and the first byte race on a fast local server, so a miss waits a short grace and looks again.
    check: async (key: Rules['bypassKey'], urlHashes: UrlHashes): Promise<boolean> => {
      if (key === 'none') return false;
      if (await held(key, urlHashes)) return true;
      const record = await current();
      if (record && now() - record.at <= OTHER_FRESH_MS) return false;
      await sleep(GRACE_MS);
      return held(key, urlHashes);
    },
  };
}
