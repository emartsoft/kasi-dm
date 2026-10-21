import type { Rules } from '../types/protocol';
import { parseRules } from './messages';

const KEY = 'rules';

export interface StorageAreaLike {
  get(key: string): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
}

// A Chromium service worker is stopped when idle, so the rules Kasi pushes live in storage.local.
export function createRulesCache(area: StorageAreaLike) {
  return {
    save: (rules: Rules): Promise<void> => area.set({ [KEY]: rules }),
    // Stored data is re-validated: anything malformed is treated as no cache.
    load: async (): Promise<Rules | undefined> => parseRules((await area.get(KEY))[KEY]),
  };
}

// The rules in use: the last message from Kasi, else the cache, so a worker started cold by a download decides as a warm one does.
export function createRulesProvider(cache: ReturnType<typeof createRulesCache>) {
  let current: Rules | undefined;
  return {
    set: (rules: Rules): void => {
      current = rules;
    },
    get: async (): Promise<Rules | undefined> => (current ??= await cache.load()),
  };
}
