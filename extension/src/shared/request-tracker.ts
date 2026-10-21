// DownloadItem has no HTTP method, so non-GET requests are remembered here (TC-BRW-004: POST-form downloads are never intercepted).
// Redirect hops are kept as well so the bypass can match a link that redirected. Only hashes are stored.
import { hashUrl } from './hash';
import type { StorageAreaLike } from './rules-cache';

const KEY = 'request-marks';
export const MARK_TTL_MS = 30_000;
const MAX_MARKS = 64;

interface Mark {
  until: number;
  post: boolean;
  from?: string;
}

function isMark(value: unknown): value is Mark {
  return (
    typeof value === 'object' &&
    value !== null &&
    'until' in value &&
    typeof value.until === 'number' &&
    'post' in value &&
    typeof value.post === 'boolean'
  );
}

export function createRequestTracker(area: StorageAreaLike, now: () => number = Date.now) {
  let marks: Map<string, Mark> | undefined;
  let queue: Promise<unknown> = Promise.resolve();

  async function load(): Promise<Map<string, Mark>> {
    if (!marks) {
      const stored = (await area.get(KEY))[KEY];
      const entries = typeof stored === 'object' && stored !== null ? Object.entries(stored) : [];
      marks = new Map(entries.filter((entry): entry is [string, Mark] => isMark(entry[1])));
    }
    for (const [hash, mark] of marks) if (mark.until <= now()) marks.delete(hash);
    return marks;
  }

  // Marks are written in the order the browser reported them, and a lookup waits for those still being written.
  function enqueue<T>(work: () => Promise<T>): Promise<T> {
    const result = queue.then(work);
    queue = result.catch(() => undefined);
    return result;
  }

  async function save(map: Map<string, Mark>): Promise<void> {
    while (map.size > MAX_MARKS) map.delete(map.keys().next().value as string);
    await area.set({ [KEY]: Object.fromEntries(map) });
  }

  return {
    onRequest: (url: string, method: string): Promise<void> =>
      enqueue(async () => {
        if (method.toUpperCase() === 'GET') return;
        const map = await load();
        map.set(await hashUrl(url), { until: now() + MARK_TTL_MS, post: true });
        await save(map);
      }),
    // A POST followed by 302 to a GET is still the POST's download.
    onRedirect: (url: string, redirectUrl: string): Promise<void> =>
      enqueue(async () => {
        const map = await load();
        const from = await hashUrl(url);
        const post = map.get(from)?.post ?? false;
        map.set(await hashUrl(redirectUrl), { until: now() + MARK_TTL_MS, post, from });
        await save(map);
      }),
    isPost: (...urls: string[]): Promise<boolean> =>
      enqueue(async () => {
        const map = await load();
        for (const url of urls) if (map.get(await hashUrl(url))?.post) return true;
        return false;
      }),
    // Hashes of the URLs that redirected to `url`, nearest first.
    hops: (url: string): Promise<string[]> =>
      enqueue(async () => {
        const map = await load();
        const result: string[] = [];
        let current = await hashUrl(url);
        for (let i = 0; i < MAX_MARKS; i++) {
          const from = map.get(current)?.from;
          if (!from) break;
          result.push(from);
          current = from;
        }
        return result;
      }),
  };
}
