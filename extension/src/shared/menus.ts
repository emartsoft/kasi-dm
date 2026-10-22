// Context menus (KASI-BRW-004, plan m2-5 2.4): "Download with Kasi" on a link or a piece of media, "Download all links" and
// "Download selected links" on a page. A click never pauses anything (there is no browser download); it waits for Kasi like the
// download hand-off does and sends one `addDownload` or one `addBatch`. Every browser API is injected.
import type { Cookie } from '../types/protocol';
import { buildAddDownload } from './build-add-download';
import { waitForKasi, type ReadyPort } from './kasi-ready';
import {
  buildAddBatch,
  isDownloadableUrl,
  normalizeLinks,
  originsOf,
  type LinkScope,
  type RawAnchor,
} from './link-collector';
import type { Logger } from './logger';
import { NativeRequestError } from './native-port';
import type { StorageAreaLike } from './rules-cache';

export const MENU_LINK = 'kasi-download-link';
export const MENU_ALL = 'kasi-download-all';
export const MENU_SELECTED = 'kasi-download-selected';
export const INSTALLED_KEY = 'menusInstalled';
/** A batch's cookies come from at most this many distinct hosts; a page of links to hundreds of sites sends the first ones' cookies. */
export const MAX_COOKIE_HOSTS = 50;

export interface MenuProperties {
  id: string;
  title: string;
  contexts: string[];
}

export interface ClickInfo {
  menuItemId: string | number;
  linkUrl?: string;
  srcUrl?: string;
  pageUrl?: string;
}

export interface ClickTab {
  id?: number;
  url?: string;
  incognito?: boolean;
}

export interface MenusDeps {
  menus: { removeAll(): Promise<void>; create(properties: MenuProperties): Promise<void> | void };
  /** A string from `_locales` by key. */
  title(key: 'menu_download_link' | 'menu_download_all' | 'menu_download_selected'): string;
  /** The raw anchors of every frame of the tab. */
  collect(tabId: number, scope: LinkScope): Promise<RawAnchor[]>;
  cookiesFor(urls: string[], incognito: boolean): Promise<Cookie[]>;
  port: ReadyPort & { request(message: { id: string }): Promise<unknown> };
  /** Survives a worker restart but not a browser restart: menus are created again once per browser session. */
  session: StorageAreaLike;
  userAgent: string;
  newId(): string;
  onDisconnected(): void;
  logger: Logger;
}

export function createMenus(deps: MenusDeps) {
  const log = deps.logger;
  let installing: Promise<void> | undefined;

  async function create(): Promise<void> {
    await deps.menus.removeAll();
    await deps.menus.create({
      id: MENU_LINK,
      title: deps.title('menu_download_link'),
      contexts: ['link', 'image', 'video', 'audio'],
    });
    await deps.menus.create({
      id: MENU_ALL,
      title: deps.title('menu_download_all'),
      contexts: ['page'],
    });
    await deps.menus.create({
      id: MENU_SELECTED,
      title: deps.title('menu_download_selected'),
      contexts: ['selection'],
    });
    await deps.session.set({ [INSTALLED_KEY]: true });
  }

  // Removing first makes this idempotent, so a second call (onInstalled and the start-up check can overlap) never duplicates a menu.
  function install(force: boolean): Promise<void> {
    installing = (installing ?? Promise.resolve()).then(async () => {
      if (!force && (await deps.session.get(INSTALLED_KEY))[INSTALLED_KEY] === true) return;
      await create();
    });
    return installing.catch((error: unknown) => {
      log.warn('Could not create the context menus', { error });
    });
  }

  async function send(message: { id: string }, what: string): Promise<void> {
    if (!(await waitForKasi(deps.port))) {
      log.info('Kasi is not reachable; the menu request was not sent');
      deps.onDisconnected();
      return;
    }
    try {
      await deps.port.request(message);
      log.info('Menu request handed to Kasi', { what });
    } catch (error) {
      const code = error instanceof NativeRequestError ? error.code : 'INTERNAL';
      log.warn('Kasi did not accept the menu request', { code });
      deps.onDisconnected();
    }
  }

  async function downloadLink(info: ClickInfo, tab: ClickTab | undefined): Promise<void> {
    const url = info.linkUrl ?? info.srcUrl;
    if (!isDownloadableUrl(url)) {
      log.info('A menu click on a link that cannot be downloaded was ignored');
      return;
    }
    const cookies = await deps.cookiesFor([url], tab?.incognito === true);
    // Kasi resolves the real name, size and type from the response (plan m2-5 2.4).
    const fields = { url, finalUrl: url, ...(info.pageUrl ? { referrer: info.pageUrl } : {}) };
    await send(buildAddDownload(deps.newId(), fields, deps.userAgent, cookies), 'download');
  }

  async function downloadPage(
    scope: LinkScope,
    info: ClickInfo,
    tab: ClickTab | undefined,
  ): Promise<void> {
    if (tab?.id === undefined) return;
    const { links, truncated } = normalizeLinks(await deps.collect(tab.id, scope));
    if (truncated) log.warn('A page had more links than one batch holds; the rest was dropped');
    const referrer = info.pageUrl ?? tab.url;
    const cookies = await deps.cookiesFor(
      [...(referrer ? [referrer] : []), ...originsOf(links, MAX_COOKIE_HOSTS)],
      tab.incognito === true,
    );
    const batch = buildAddBatch(deps.newId(), referrer, links, deps.userAgent, cookies);
    if (!batch) {
      log.info('A menu click found no links to send', { count: links.length });
      return;
    }
    await send(batch, 'batch');
  }

  return {
    /** From runtime.onInstalled (force) and at every background start (once per browser session). */
    install: (force = false): Promise<void> => install(force),
    // Called for every contextMenus.onClicked; never throws.
    onClicked: async (info: ClickInfo, tab?: ClickTab): Promise<void> => {
      try {
        if (info.menuItemId === MENU_LINK) await downloadLink(info, tab);
        else if (info.menuItemId === MENU_ALL) await downloadPage('all', info, tab);
        else if (info.menuItemId === MENU_SELECTED) await downloadPage('selection', info, tab);
      } catch (error) {
        log.error('A menu click failed', { error });
      }
    },
  };
}

// A menu click sent as a message by one of this extension's own pages (the options page). Real clicks come from contextMenus.onClicked;
// this carries the same data into the same handler so the end-to-end suites can exercise it, because WebDriver and Playwright cannot
// click a native menu. The background accepts it only from a page of this extension, never from a content script or a web page.
export const MENU_CLICK_KIND = 'kasi-menu-click';

export interface MenuClickMessage {
  kind: typeof MENU_CLICK_KIND;
  info: ClickInfo;
  tab: ClickTab;
}

export function parseMenuClick(value: unknown): MenuClickMessage | undefined {
  if (typeof value !== 'object' || value === null) return undefined;
  const { kind, info, tab } = value as Record<string, unknown>;
  if (kind !== MENU_CLICK_KIND || typeof info !== 'object' || info === null) return undefined;
  if (typeof tab !== 'object' || tab === null) return undefined;
  const { menuItemId, linkUrl, srcUrl, pageUrl } = info as Record<string, unknown>;
  const { id, url, incognito } = tab as Record<string, unknown>;
  if (menuItemId !== MENU_LINK && menuItemId !== MENU_ALL && menuItemId !== MENU_SELECTED) {
    return undefined;
  }
  const info2: ClickInfo = { menuItemId };
  for (const [key, value] of [
    ['linkUrl', linkUrl],
    ['srcUrl', srcUrl],
    ['pageUrl', pageUrl],
  ] as const) {
    if (typeof value === 'string' && value) info2[key] = value;
  }
  const tab2: ClickTab = {};
  if (typeof id === 'number') tab2.id = id;
  if (typeof url === 'string' && url) tab2.url = url;
  if (incognito === true) tab2.incognito = true;
  return { kind: MENU_CLICK_KIND, info: info2, tab: tab2 };
}
