import browser, { type Runtime } from 'webextension-polyfill';
import type { OpenSettings } from '../types/protocol';
import { createLogger } from '../shared/logger';
import { createNativePort, NATIVE_HOST } from '../shared/native-port';
import { createRulesCache } from '../shared/rules-cache';
import { isStatusRequest } from '../shared/status';

const logger = createLogger();
const rulesCache = createRulesCache(browser.storage.local);

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
    rulesCache.save(rules).catch((error: unknown) => {
      logger.error('Could not cache the rules', { error });
    });
  },
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
  if (isOpenSettings(message)) return nativePort.request(message);
  return undefined;
});

nativePort.start();
