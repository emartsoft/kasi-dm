// Refreshing the link of a paused job (TRD 6.6, plan m2-5 2.5). Kasi sends `refreshLink`; the referrer page is opened in a
// background tab and the first download it starts (matching the hint, when there is one) is taken through the `claims` seam of the
// interception: cancelled and erased, its final URL and cookies sent as `linkRefreshed`. The tab is closed in every path.
import type { Cookie, LinkRefreshed, RefreshLink } from '../types/protocol';
import type { DownloadItemLike } from './interception';
import { isDownloadableUrl } from './link-collector';
import type { Logger } from './logger';

/** A page that has not started a download by now is given up on and nothing is sent. */
export const REFRESH_TIMEOUT_MS = 60_000;

export interface RefreshLinkDeps {
  tabs: { create(url: string): Promise<{ id?: number }>; remove(tabId: number): Promise<void> };
  downloads: {
    cancel(id: number): Promise<void>;
    erase(query: { id: number }): Promise<unknown>;
    removeFile?(id: number): Promise<void>;
  };
  cookiesFor(item: DownloadItemLike): Promise<Cookie[]>;
  port: { request(message: { id: string }): Promise<unknown> };
  newId(): string;
  logger: Logger;
  timeoutMs?: number;
}

interface Watch {
  message: RefreshLink;
  tabId: number | undefined;
  timer: ReturnType<typeof setTimeout>;
}

function lastSegment(url: string): string {
  try {
    return decodeURIComponent(new URL(url).pathname.split('/').pop() ?? '');
  } catch {
    return '';
  }
}

// The file name is not always known when the download is created, so the URL's last segment counts too.
function matches(hint: string | undefined, item: DownloadItemLike): boolean {
  if (hint === undefined) return true;
  const names = [
    (item.filename ?? '').replaceAll('\\', '/').split('/').pop() ?? '',
    lastSegment(item.finalUrl ?? item.url),
    lastSegment(item.url),
  ];
  const wanted = hint.toLowerCase();
  return names.some((name) => name.toLowerCase().includes(wanted));
}

export function createRefreshLink(deps: RefreshLinkDeps) {
  const log = deps.logger;
  const watches = new Map<string, Watch>();

  async function finish(jobId: string): Promise<void> {
    const watch = watches.get(jobId);
    if (!watch) return;
    watches.delete(jobId);
    clearTimeout(watch.timer);
    if (watch.tabId === undefined) return;
    try {
      await deps.tabs.remove(watch.tabId);
    } catch {
      // the user closed it already
    }
  }

  async function drop(id: number): Promise<void> {
    await deps.downloads.cancel(id);
    try {
      await deps.downloads.removeFile?.(id);
    } catch {
      // nothing was left to remove
    }
    await deps.downloads.erase({ id });
  }

  return {
    /** Kasi's `refreshLink`: open the page and wait for its download. A second request for the same job replaces the first. */
    start: async (message: RefreshLink): Promise<void> => {
      await finish(message.jobId);
      const watch: Watch = {
        message,
        tabId: undefined,
        timer: setTimeout(() => {
          log.info('The page started no download; the link was not refreshed');
          void finish(message.jobId);
        }, deps.timeoutMs ?? REFRESH_TIMEOUT_MS),
      };
      // Registered before the tab opens: a download can start before create() returns.
      watches.set(message.jobId, watch);
      try {
        watch.tabId = (await deps.tabs.create(message.referrerUrl)).id;
      } catch (error) {
        log.warn('Could not open the page to refresh a link', { error });
        await finish(message.jobId);
        return;
      }
      // finish() ran while the tab was opening (timeout or a claimed download): close it now.
      if (watches.get(message.jobId) !== watch && watch.tabId !== undefined) {
        try {
          await deps.tabs.remove(watch.tabId);
        } catch {
          // already gone
        }
      }
    },

    /** An entry for `interception`'s claims: true when the download answered a refresh and nothing else may touch it. */
    claim: async (item: DownloadItemLike): Promise<boolean> => {
      const entry = [...watches.entries()].find(([, w]) => matches(w.message.matchHint, item));
      if (!entry) return false;
      const [jobId] = entry;
      const url = item.finalUrl ?? item.url;
      try {
        // The watch ends first so a second download cannot be taken for the same job.
        await finish(jobId);
        await drop(item.id);
        if (!isDownloadableUrl(url)) {
          log.warn('The refreshed download has a URL Kasi cannot take');
          return true;
        }
        const reply: LinkRefreshed = {
          v: 1,
          id: deps.newId(),
          type: 'linkRefreshed',
          jobId,
          url,
          cookies: await deps.cookiesFor(item),
        };
        await deps.port.request(reply);
        log.info('Link refreshed', { downloadId: item.id });
      } catch (error) {
        log.warn('Could not send the refreshed link', { error });
      }
      return true;
    },
  };
}
