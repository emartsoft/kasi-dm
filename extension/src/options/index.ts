import browser from 'webextension-polyfill';
import type { Ack, OpenSettings } from '../types/protocol';

function isMatchingAck(value: unknown, id: string): value is Ack {
  return (
    typeof value === 'object' &&
    value !== null &&
    'v' in value &&
    value.v === 1 &&
    'type' in value &&
    value.type === 'ack' &&
    'id' in value &&
    value.id === id &&
    'ok' in value &&
    value.ok === true
  );
}

const title = document.querySelector<HTMLHeadingElement>('#title');
const button = document.querySelector<HTMLButtonElement>('#open-settings');
const status = document.querySelector<HTMLParagraphElement>('#status');

if (title && button && status) {
  title.textContent = browser.i18n.getMessage('extensionName');
  button.textContent = browser.i18n.getMessage('openSettings');
  status.textContent = browser.i18n.getMessage('connectionUnavailable');

  button.addEventListener('click', () => {
    const request = {
      v: 1,
      id: crypto.randomUUID(),
      type: 'openSettings',
      section: 'browser',
    } satisfies OpenSettings;

    // M2.3b supplies the background listener and native port manager.
    void browser.runtime.sendMessage(request).then(
      (response: unknown) => {
        status.textContent = browser.i18n.getMessage(
          isMatchingAck(response, request.id) ? 'settingsRequestSent' : 'connectionUnavailable',
        );
      },
      () => {
        status.textContent = browser.i18n.getMessage('connectionUnavailable');
      },
    );
  });
}
