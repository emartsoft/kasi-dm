// Download interception (KASI-BRW-002, BRW-006; plan 2.2). A matching download is paused, handed to Kasi, and cancelled in the
// browser only after Kasi acknowledges; every other path resumes it.
import type { Cookie, Rules } from '../types/protocol';
import { buildAddDownload } from './build-add-download';
import { hashUrl } from './hash';
import type { Logger } from './logger';
import { NativeRequestError, type PortFailure } from './native-port';
import { waitForKasi } from './kasi-ready';
import { isEligible, matchesRules } from './rules-match';

export { READY_WAIT_MS } from './kasi-ready';
const LATE_ACK_MS = 30_000;

export interface DownloadItemLike {
  id: number;
  url: string;
  finalUrl?: string;
  referrer?: string;
  filename?: string;
  mime?: string;
  totalBytes?: number;
  state?: string;
  paused?: boolean;
  canResume?: boolean;
  incognito?: boolean;
  byExtensionId?: string;
}

export interface InterceptionDeps {
  downloads: {
    pause(id: number): Promise<void>;
    resume(id: number): Promise<void>;
    cancel(id: number): Promise<void>;
    erase(query: { id: number }): Promise<unknown>;
    search(query: { id: number }): Promise<DownloadItemLike[]>;
    // cancel does not delete a part file left by Firefox, nor a download that completed while paused (its bytes were all in).
    removeFile?(id: number): Promise<void>;
  };
  port: {
    isConnected(): boolean;
    lastFailure(): PortFailure | undefined;
    reconnectNow(): void;
    whenConnected(timeoutMs: number): Promise<boolean>;
    request(message: { id: string }): Promise<unknown>;
  };
  getRules(): Promise<Rules | undefined>;
  // The popup's toggle (settings.ts). Off: nothing is intercepted, claims still run.
  isEnabled(): Promise<boolean>;
  isIncognitoAllowed(): Promise<boolean>;
  hasHostPermission(): Promise<boolean>;
  isPost(...urls: string[]): Promise<boolean>;
  hops(url: string): Promise<string[]>;
  bypassHeld(key: Rules['bypassKey'], urlHashes: string[]): Promise<boolean>;
  cookiesFor(item: DownloadItemLike): Promise<Cookie[]>;
  userAgent: string;
  newId(): string;
  // Shown as the red badge (BRW-006); cleared by the caller when rules arrive.
  onDisconnected(): void;
  logger: Logger;
  now?: () => number;
  // A refresh watcher can take a download before the rules see it (plan 2.9). Empty in 2.4.
  claims?: ((item: DownloadItemLike) => boolean | Promise<boolean>)[];
}

export function createInterception(deps: InterceptionDeps) {
  const now = deps.now ?? Date.now;
  const log = deps.logger;
  // Requests that timed out, by request id, so a late ack can find its download.
  const unanswered = new Map<string, { downloadId: number; at: number }>();

  async function resumeIfPaused(id: number): Promise<void> {
    try {
      const [item] = await deps.downloads.search({ id });
      // A download the user cancelled (or one that finished) meanwhile is left alone. Firefox reports a paused download as
      // 'interrupted' (error USER_CANCELED) with paused and canResume set, so the state alone cannot tell the two apart.
      if (item?.paused && item.canResume !== false && item.state !== 'complete') {
        await deps.downloads.resume(id);
      }
    } catch (error) {
      log.warn('Could not resume a download', { error });
    }
  }

  async function dropFromBrowser(id: number): Promise<void> {
    await deps.downloads.cancel(id);
    if (deps.downloads.removeFile) {
      try {
        await deps.downloads.removeFile(id);
      } catch {
        // nothing was left to remove
      }
    }
    await deps.downloads.erase({ id });
  }

  function giveUp(id: number): Promise<void> {
    deps.onDisconnected();
    return resumeIfPaused(id);
  }

  const ready = (): Promise<boolean> => waitForKasi(deps.port);

  async function wanted(item: DownloadItemLike, rules: Rules): Promise<boolean> {
    const incognitoAllowed = item.incognito ? await deps.isIncognitoAllowed() : false;
    if (!isEligible(item, incognitoAllowed) || !matchesRules(rules, item)) return false;
    if (item.state !== undefined && item.state !== 'in_progress') return false;
    if (await deps.isPost(item.url, item.finalUrl ?? item.url)) return false;
    return deps.hasHostPermission();
  }

  async function handOff(item: DownloadItemLike, rules: Rules): Promise<void> {
    const hashes = await Promise.all(
      [item.url, item.finalUrl ?? item.url].map((url) => hashUrl(url)),
    );
    const chain = await deps.hops(item.finalUrl ?? item.url);
    if (await deps.bypassHeld(rules.bypassKey, [...hashes, ...chain])) {
      await resumeIfPaused(item.id);
      return;
    }
    if (!(await ready())) {
      log.info('Kasi is not reachable; the browser keeps the download');
      await giveUp(item.id);
      return;
    }
    const id = deps.newId();
    const message = buildAddDownload(id, item, deps.userAgent, await deps.cookiesFor(item));
    try {
      await deps.port.request(message);
    } catch (error) {
      const code = error instanceof NativeRequestError ? error.code : 'INTERNAL';
      if (code === 'TIMEOUT') unanswered.set(id, { downloadId: item.id, at: now() });
      log.warn('Kasi did not accept the download; the browser keeps it', { code });
      await giveUp(item.id);
      return;
    }
    log.info('Download handed to Kasi', { downloadId: item.id });
    try {
      await dropFromBrowser(item.id);
    } catch (error) {
      // Kasi owns the job now; resuming here would download the file twice.
      log.warn('Kasi has the download but the browser copy could not be removed', { error });
    }
  }

  return {
    // Called for every downloads.onCreated; never throws.
    handle: async (item: DownloadItemLike): Promise<void> => {
      const ours = { paused: false };
      try {
        for (const claim of deps.claims ?? []) if (await claim(item)) return;
        if (!(await deps.isEnabled())) return;
        const rules = await deps.getRules();
        if (!rules || !(await wanted(item, rules))) return;
        // Pause first (TRD 9.2) so no bytes are written while Kasi decides.
        try {
          await deps.downloads.pause(item.id);
          ours.paused = true;
        } catch {
          return; // already finished, or gone
        }
        await handOff(item, rules);
      } catch (error) {
        log.error('Interception failed; the browser keeps the download', { error });
        if (ours.paused) await giveUp(item.id);
      }
    },
    // An ack after the timeout: the download was resumed, so end the browser's copy if it is still running.
    lateAck: async (requestId: string): Promise<void> => {
      const entry = unanswered.get(requestId);
      unanswered.delete(requestId);
      if (!entry || now() - entry.at > LATE_ACK_MS) return;
      try {
        const [item] = await deps.downloads.search({ id: entry.downloadId });
        // Bytes already complete are left to Kasi's duplicate prompt (ENG-013).
        if (item?.state === 'in_progress') await dropFromBrowser(entry.downloadId);
      } catch (error) {
        log.warn('Could not cancel after a late ack', { error });
      }
    },
  };
}
