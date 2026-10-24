import browser, { type Runtime } from 'webextension-polyfill';
import type { OpenSettings } from '../types/protocol';
import { createLogger } from '../shared/logger';
import { createNativePort, NATIVE_HOST } from '../shared/native-port';
import { createBypass, parseModifierMessage } from '../shared/bypass';
import { collectCookies, storeFor } from '../shared/cookies';
import { createInterception, type DownloadItemLike } from '../shared/interception';
import { collectPageLinks, parseRawAnchors, type LinkScope } from '../shared/link-collector';
import {
  createMediaDetector,
  parseMediaRuntimeMessage,
  type MediaRuntimeMessage,
} from '../shared/media-controller';
import { createMediaSession } from '../shared/media-session';
import { createMenus, parseMenuClick } from '../shared/menus';
import { createRefreshLink } from '../shared/refresh-link';
import { createRequestTracker } from '../shared/request-tracker';
import { createRulesCache, createRulesProvider } from '../shared/rules-cache';
import { INTERCEPTION_KEY, readInterceptionEnabled } from '../shared/settings';
import { isStatusRequest } from '../shared/status';

const logger = createLogger();
const rulesCache = createRulesCache(browser.storage.local);
const rulesInUse = createRulesProvider(rulesCache);
const tracker = createRequestTracker(browser.storage.session);
const bypass = createBypass({ area: browser.storage.session });

function clientName(): 'chrome' | 'edge' | 'firefox' {
  const agent = navigator.userAgent;
  if (agent.includes('Firefox/')) return 'firefox';
  return agent.includes('Edg/') ? 'edge' : 'chrome';
}

// The toolbar badge shows the worst state: `!` when Kasi cannot be reached, `off` when the user switched interception off.
// Both inputs are mirrored in memory so the `!` is set in the same tick as the notice that Kasi is gone (the 5 s of BRW-006).
const badgeState = { disconnected: false, interceptionOn: true };

function paintBadge(): void {
  const text = badgeState.disconnected
    ? browser.i18n.getMessage('badge_disconnected')
    : badgeState.interceptionOn
      ? ''
      : browser.i18n.getMessage('badge_off');
  void browser.action.setBadgeText({ text });
  if (badgeState.disconnected) void browser.action.setBadgeBackgroundColor({ color: '#c42b1c' });
}

function showDisconnected(): void {
  badgeState.disconnected = true;
  paintBadge();
}

// refreshLink (plan m2-5 2.5): a background tab on the referrer page, and a claim on the download it starts.
const refresh = createRefreshLink({
  tabs: {
    create: (url) => browser.tabs.create({ url, active: false }),
    remove: (tabId) => browser.tabs.remove(tabId),
  },
  downloads: {
    cancel: (id) => browser.downloads.cancel(id),
    erase: (query) => browser.downloads.erase(query),
    removeFile: (id: number) => browser.downloads.removeFile(id),
  },
  cookiesFor: async (item) => {
    const urls = [item.url, item.finalUrl ?? item.url];
    const storeId = await storeFor(browser.cookies, item.incognito === true);
    return collectCookies(browser.cookies, urls, storeId);
  },
  port: { request: (message) => nativePort.request(message) },
  newId: () => crypto.randomUUID(),
  logger,
});

const nativePort = createNativePort({
  connect: () => browser.runtime.connectNative(NATIVE_HOST),
  client: clientName(),
  extensionVersion: browser.runtime.getManifest().version,
  logger,
  onRules: (rules) => {
    rulesInUse.set(rules);
    badgeState.disconnected = false;
    paintBadge();
    rulesCache.save(rules).catch((error: unknown) => {
      logger.error('Could not cache the rules', { error });
    });
  },
  // An ack that came after the timeout: the download was resumed, so end the browser's copy (plan 2.3).
  onLateAck: (id) => {
    void interception.lateAck(id);
  },
  onLost: showDisconnected,
  onRefreshLink: (message) => {
    void refresh.start(message);
  },
});

// Media detection (KASI-MED-001): classification, the session list, the badge and the page/popup messages.
const mediaSession = createMediaSession(browser.storage.session);
const mediaDetector = createMediaDetector({
  store: mediaSession,
  badge: {
    set: (tabId, count) => {
      void browser.action.setBadgeText({ tabId, text: String(count) });
      void browser.action.setBadgeBackgroundColor({ tabId, color: '#0f6cbd' });
    },
    clear: (tabId) => {
      void browser.action.setBadgeText({ tabId, text: '' });
    },
  },
  tabs: {
    sendMessage: async (tabId, message) => {
      await browser.tabs.sendMessage(tabId, message);
    },
  },
  port: nativePort,
  cookiesFor: async (url, tabId) => {
    const tab = await browser.tabs.get(tabId);
    return collectCookies(browser.cookies, [url], await storeFor(browser.cookies, tab.incognito));
  },
  userAgent: navigator.userAgent,
  now: () => Date.now(),
  newId: () => crypto.randomUUID(),
  logger,
});

async function handleMediaMessage(
  message: MediaRuntimeMessage,
  sender: Runtime.MessageSender,
): Promise<unknown> {
  const tabId = 'tabId' in message ? (message.tabId ?? sender.tab?.id) : sender.tab?.id;
  if (tabId === undefined || tabId < 0) return { ok: false };
  switch (message.type) {
    case 'mediaPage':
      await mediaDetector.onPageContext(tabId, {
        pageUrl: message.pageUrl,
        ...(message.title ? { title: message.title } : {}),
        ...(message.thumbnailUrl ? { thumbnailUrl: message.thumbnailUrl } : {}),
      });
      return { ok: true };
    case 'mediaList':
      return { ok: true, media: await mediaDetector.list(tabId) };
    case 'mediaCapture':
    case 'mediaOverlay': {
      const capture = await mediaDetector.capture(
        tabId,
        message.type === 'mediaCapture' ? message.url : undefined,
      );
      if (!capture) return { ok: false, code: 'NO_MEDIA' };
      try {
        return await nativePort.request(capture);
      } catch {
        return { ok: false, code: 'APP_NOT_RUNNING' };
      }
    }
    default:
      return undefined;
  }
}

const interception = createInterception({
  downloads: {
    pause: (id) => browser.downloads.pause(id),
    resume: (id) => browser.downloads.resume(id),
    cancel: (id) => browser.downloads.cancel(id),
    erase: (query) => browser.downloads.erase(query),
    search: (query) => browser.downloads.search(query),
    // cancel leaves the part file behind on Firefox, and does nothing to a download that finished while Kasi was answering
    removeFile: (id: number) => browser.downloads.removeFile(id),
  },
  port: nativePort,
  getRules: rulesInUse.get,
  isEnabled: () => readInterceptionEnabled(browser.storage.local),
  isIncognitoAllowed: () => browser.extension.isAllowedIncognitoAccess(),
  hasHostPermission: () => browser.permissions.contains({ origins: ['<all_urls>'] }),
  isPost: tracker.isPost,
  hops: tracker.hops,
  bypassHeld: bypass.check,
  cookiesFor: async (item) => {
    const urls = [item.url, item.finalUrl ?? item.url];
    const storeId = await storeFor(browser.cookies, item.incognito === true);
    return collectCookies(browser.cookies, urls, storeId);
  },
  userAgent: navigator.userAgent,
  newId: () => crypto.randomUUID(),
  onDisconnected: showDisconnected,
  logger,
  claims: [(item) => refresh.claim(item)],
});

// Context menus (plan m2-5 2.4). The links of every frame come from scripting.executeScript, which needs no content-script message.
const menus = createMenus({
  menus: {
    removeAll: () => browser.contextMenus.removeAll(),
    create: (properties) =>
      new Promise<void>((resolve) => {
        browser.contextMenus.create(
          { id: properties.id, title: properties.title, contexts: properties.contexts as never },
          () => {
            resolve();
          },
        );
      }),
  },
  title: (key) => browser.i18n.getMessage(key),
  collect: async (tabId: number, scope: LinkScope) => {
    const frames = await browser.scripting.executeScript({
      target: { tabId, allFrames: true },
      func: collectPageLinks,
      args: [scope],
    });
    return frames.flatMap((frame) => parseRawAnchors(frame.result));
  },
  cookiesFor: async (urls, incognito) =>
    collectCookies(browser.cookies, urls, await storeFor(browser.cookies, incognito)),
  port: nativePort,
  session: browser.storage.session,
  userAgent: navigator.userAgent,
  newId: () => crypto.randomUUID(),
  onDisconnected: showDisconnected,
  logger,
});

function isOpenSettings(value: unknown): value is OpenSettings {
  return (
    typeof value === 'object' &&
    value !== null &&
    'type' in value &&
    value.type === 'openSettings' &&
    'id' in value &&
    typeof value.id === 'string'
  );
}

// Listeners are registered synchronously so a stopped worker or event page wakes for them.
browser.runtime.onMessage.addListener((message: unknown, sender: Runtime.MessageSender) => {
  if (sender.id !== browser.runtime.id) return undefined;
  const mediaMessage = parseMediaRuntimeMessage(message);
  if (mediaMessage) return handleMediaMessage(mediaMessage, sender);
  if (isStatusRequest(message)) {
    // The popup is a reason to try again (plan m2-5 decision 3); the answer is the state as it is now.
    nativePort.reconnectForStatus();
    return Promise.resolve({ connected: nativePort.isConnected() });
  }
  const modifier = parseModifierMessage(message);
  if (modifier) return bypass.record(modifier, sender.tab?.id);
  if (isOpenSettings(message)) return nativePort.request(message);
  const menuClick = parseMenuClick(message);
  // Only a page of this extension may stand in for a menu click (see parseMenuClick), never a content script or a web page.
  if (menuClick && sender.url?.startsWith(browser.runtime.getURL(''))) {
    return menus.onClicked(menuClick.info, menuClick.tab);
  }
  return undefined;
});

browser.runtime.onInstalled.addListener(() => {
  void menus.install(true);
});
browser.contextMenus.onClicked.addListener((info, tab) => {
  void menus.onClicked(info, tab);
});

browser.downloads.onCreated.addListener((item: DownloadItemLike) => {
  void interception.handle(item);
});

const webRequestFilter = { urls: ['<all_urls>'], types: ['main_frame', 'sub_frame'] } as const;
browser.webRequest.onBeforeRequest.addListener(
  (details) => {
    void tracker.onRequest(details.url, details.method);
  },
  { urls: [...webRequestFilter.urls], types: [...webRequestFilter.types] },
);
browser.webRequest.onBeforeRedirect.addListener(
  (details) => {
    void tracker.onRedirect(details.url, details.redirectUrl);
  },
  { urls: [...webRequestFilter.urls], types: [...webRequestFilter.types] },
);

browser.webRequest.onResponseStarted.addListener(
  (details) => {
    void mediaDetector.onResponse(details);
  },
  { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other', 'main_frame'] },
  ['responseHeaders'],
);

browser.tabs.onRemoved.addListener((tabId) => {
  void mediaDetector.onTabRemoved(tabId);
});

browser.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local' || !(INTERCEPTION_KEY in changes)) return;
  badgeState.interceptionOn = changes[INTERCEPTION_KEY].newValue !== false;
  paintBadge();
});
void readInterceptionEnabled(browser.storage.local).then((on) => {
  badgeState.interceptionOn = on;
  paintBadge();
});

// The menus survive a worker restart but not always a browser restart: create them once per browser session.
void menus.install(false);
nativePort.start();
