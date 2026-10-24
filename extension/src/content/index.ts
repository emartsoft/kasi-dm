import browser from 'webextension-polyfill';
import { installMediaOverlay } from '../shared/media-overlay';
import { installModifierReporter, type EventRoot } from './modifier';

// Page features other than the bypass key arrive in M2.5.
installModifierReporter(document as unknown as EventRoot, (message) => {
  browser.runtime.sendMessage(message).catch(() => undefined);
});

// The overlay is the page's visible confirm step; the list itself lives in the popup. The worker owns the detection list.
const startOverlay = (): void => {
  installMediaOverlay(
    document,
    (message) => {
      browser.runtime.sendMessage(message).catch(() => undefined);
    },
    browser.i18n.getMessage('media_overlay_save'),
    location.href,
    document,
    (context) => {
      browser.runtime.sendMessage(context).catch(() => undefined);
    },
  );
};

if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', startOverlay, { once: true });
} else {
  startOverlay();
}
