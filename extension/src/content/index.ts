import browser from 'webextension-polyfill';
import { installModifierReporter, type EventRoot } from './modifier';

// Page features other than the bypass key arrive in M2.5.
installModifierReporter(document as unknown as EventRoot, (message) => {
  browser.runtime.sendMessage(message).catch(() => undefined);
});
