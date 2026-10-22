import browser, { type Runtime } from 'webextension-polyfill';
import type { OpenSettings } from '../types/protocol';
import { createLogger } from '../shared/logger';
import { createNativePort, NATIVE_HOST } from '../shared/native-port';
import { createBypass, parseModifierMessage } from '../shared/bypass';
import { collectCookies, storeFor } from '../shared/cookies';
import { createInterception, type DownloadItemLike } from '../shared/interception';
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
});

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
  if (isStatusRequest(message)) {
    // The popup is a reason to try again (plan m2-5 decision 3); the answer is the state as it is now.
    nativePort.reconnectForStatus();
    return Promise.resolve({ connected: nativePort.isConnected() });
  }
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

browser.storage.onChanged.addListener((changes, area) => {
  if (area !== 'local' || !(INTERCEPTION_KEY in changes)) return;
  badgeState.interceptionOn = changes[INTERCEPTION_KEY].newValue !== false;
  paintBadge();
});
void readInterceptionEnabled(browser.storage.local).then((on) => {
  badgeState.interceptionOn = on;
  paintBadge();
});

nativePort.start();
