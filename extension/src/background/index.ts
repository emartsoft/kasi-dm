import browser, { type Runtime } from 'webextension-polyfill';
import type { OpenSettings } from '../types/protocol';
import { createLogger } from '../shared/logger';
import { createNativePort, NATIVE_HOST } from '../shared/native-port';
import { createBypass, parseModifierMessage } from '../shared/bypass';
import { collectCookies, storeFor } from '../shared/cookies';
import { createInterception, type DownloadItemLike } from '../shared/interception';
import { createRequestTracker } from '../shared/request-tracker';
import { createRulesCache, createRulesProvider } from '../shared/rules-cache';
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

const nativePort = createNativePort({
  connect: () => browser.runtime.connectNative(NATIVE_HOST),
  client: clientName(),
  extensionVersion: browser.runtime.getManifest().version,
  logger,
  onRules: (rules) => {
    rulesInUse.set(rules);
    void browser.action.setBadgeText({ text: '' });
    rulesCache.save(rules).catch((error: unknown) => {
      logger.error('Could not cache the rules', { error });
    });
  },
  // An ack that came after the timeout: the download was resumed, so end the browser's copy (plan 2.3).
  onLateAck: (id) => {
    void interception.lateAck(id);
  },
});

const interception = createInterception({
  downloads: {
    pause: (id) => browser.downloads.pause(id),
    resume: (id) => browser.downloads.resume(id),
    cancel: (id) => browser.downloads.cancel(id),
    erase: (query) => browser.downloads.erase(query),
    search: (query) => browser.downloads.search(query),
    // Firefox's cancel can leave the part file behind.
    ...(clientName() === 'firefox'
      ? { removeFile: (id: number) => browser.downloads.removeFile(id) }
      : {}),
  },
  port: nativePort,
  getRules: rulesInUse.get,
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
  onDisconnected: () => {
    void browser.action.setBadgeText({ text: browser.i18n.getMessage('badge_disconnected') });
    void browser.action.setBadgeBackgroundColor({ color: '#c42b1c' });
  },
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
  if (isStatusRequest(message)) return Promise.resolve({ connected: nativePort.isConnected() });
  const modifier = parseModifierMessage(message);
  if (modifier) return bypass.record(modifier, sender.tab?.id);
  if (isOpenSettings(message)) return nativePort.request(message);
  return undefined;
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

nativePort.start();
