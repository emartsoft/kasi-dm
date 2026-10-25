// Per-tab media detections in browser.storage.session (KASI-MED-001). The MV3 worker may be unloaded at any time, so the
// data is only ever read from and written to the storage area; the in-memory promise is a mutation queue, not state.

import type { MediaKind } from '../types/protocol';

export const MEDIA_STORAGE_KEY = 'kasi.media';
export const MAX_DETECTIONS_PER_TAB = 50;
export const MAX_TABS = 256;

export interface MediaDetection {
  tabId: number;
  pageUrl: string;
  kind: MediaKind;
  url: string;
  title?: string;
  thumbnailUrl?: string;
  contentType?: string;
  detectedAt: number;
}

export interface PageContext {
  pageUrl: string;
  title?: string;
  thumbnailUrl?: string;
}

interface StoredMedia {
  tabs: Record<string, MediaDetection[]>;
  contexts: Record<string, PageContext>;
}

export interface SessionArea {
  get(keys: string | string[]): Promise<Record<string, unknown>>;
  set(items: Record<string, unknown>): Promise<void>;
}

const EMPTY: StoredMedia = { tabs: {}, contexts: {} };

function clone(media: StoredMedia): StoredMedia {
  return { tabs: { ...media.tabs }, contexts: { ...media.contexts } };
}

function recordOf(value: unknown): StoredMedia {
  if (typeof value !== 'object' || value === null) return clone(EMPTY);
  const media = value as Partial<StoredMedia>;
  return {
    tabs: { ...(media.tabs ?? {}) },
    contexts: { ...(media.contexts ?? {}) },
  };
}

export interface MediaSessionStore {
  add(detection: MediaDetection): Promise<boolean>;
  list(tabId: number): Promise<MediaDetection[]>;
  all(): Promise<MediaDetection[]>;
  context(tabId: number): Promise<PageContext | undefined>;
  setContext(tabId: number, context: PageContext): Promise<void>;
  removeTab(tabId: number): Promise<void>;
  clear(): Promise<void>;
}

export function createMediaSession(area: SessionArea): MediaSessionStore {
  let tail: Promise<unknown> = Promise.resolve();

  function serialized<T>(work: () => Promise<T>): Promise<T> {
    const run = tail.then(work, work);
    tail = run.then(
      () => undefined,
      () => undefined,
    );
    return run;
  }

  async function read(): Promise<StoredMedia> {
    const values = await area.get(MEDIA_STORAGE_KEY);
    return recordOf(values[MEDIA_STORAGE_KEY]);
  }

  async function write(media: StoredMedia): Promise<void> {
    await area.set({ [MEDIA_STORAGE_KEY]: media });
  }

  function withContext(media: StoredMedia, tabId: number): MediaDetection[] {
    const context = media.contexts[String(tabId)];
    return (media.tabs[String(tabId)] ?? []).map((detection) => ({
      ...detection,
      ...(detection.title === undefined && context?.title ? { title: context.title } : {}),
      ...(detection.thumbnailUrl === undefined && context?.thumbnailUrl
        ? { thumbnailUrl: context.thumbnailUrl }
        : {}),
    }));
  }

  return {
    add: (detection) =>
      serialized(async () => {
        const media = await read();
        const key = String(detection.tabId);
        const current = media.tabs[key] ?? [];
        const at = current.findIndex((item) => item.url === detection.url);
        if (at >= 0) {
          current[at] = { ...current[at], ...detection };
        } else {
          current.push(detection);
        }

        current.sort((left, right) => left.detectedAt - right.detectedAt);
        while (current.length > MAX_DETECTIONS_PER_TAB) current.shift();
        media.tabs[key] = current;

        const tabKeys = Object.keys(media.tabs);
        if (tabKeys.length > MAX_TABS) {
          const oldest = tabKeys.sort((left, right) => {
            const a = media.tabs[left] ?? [];
            const b = media.tabs[right] ?? [];
            return (a.at(-1)?.detectedAt ?? 0) - (b.at(-1)?.detectedAt ?? 0);
          })[0];
          if (oldest !== undefined) {
            Reflect.deleteProperty(media.tabs, oldest);
            Reflect.deleteProperty(media.contexts, oldest);
          }
        }

        await write(media);
        return at < 0;
      }),

    list: (tabId) =>
      serialized(async () => {
        const media = await read();
        return withContext(media, tabId);
      }),

    all: () =>
      serialized(async () => {
        const media = await read();
        return Object.keys(media.tabs).flatMap((key) => withContext(media, Number(key)));
      }),

    context: (tabId) =>
      serialized(async () => {
        const media = await read();
        return media.contexts[String(tabId)];
      }),

    setContext: (tabId, context) =>
      serialized(async () => {
        const media = await read();
        const key = String(tabId);
        const previous = media.contexts[key];
        if (previous && previous.pageUrl !== context.pageUrl) {
          Reflect.deleteProperty(media.tabs, key);
        }
        media.contexts[key] = context;
        await write(media);
      }),

    removeTab: (tabId) =>
      serialized(async () => {
        const media = await read();
        Reflect.deleteProperty(media.tabs, String(tabId));
        Reflect.deleteProperty(media.contexts, String(tabId));
        await write(media);
      }),

    clear: () =>
      serialized(async () => {
        await write(clone(EMPTY));
      }),
  };
}
