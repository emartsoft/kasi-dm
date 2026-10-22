import browser from 'webextension-polyfill';
import { parseAck } from '../shared/messages';
import { ALL_SITES, renderStatus } from '../shared/connection-view';
import type { OpenSettings } from '../types/protocol';

const title = document.querySelector<HTMLHeadingElement>('#title');
const button = document.querySelector<HTMLButtonElement>('#open-settings');
const status = document.querySelector<HTMLParagraphElement>('#status');
const allow = document.querySelector<HTMLButtonElement>('#allow-sites');
const note = document.querySelector<HTMLParagraphElement>('#note');

if (title && button && status && allow && note) {
  note.textContent = browser.i18n.getMessage('optionsNote');
  title.textContent = browser.i18n.getMessage('extensionName');
  button.textContent = browser.i18n.getMessage('openSettings');
  allow.textContent = browser.i18n.getMessage('allowAllSites');
  const render = () => renderStatus(status, allow);
  void render();

  allow.addEventListener('click', () => {
    // permissions.request needs this user gesture.
    void browser.permissions.request(ALL_SITES).then(render, render);
  });

  button.addEventListener('click', () => {
    const request = {
      v: 1,
      id: crypto.randomUUID(),
      type: 'openSettings',
      section: 'browser',
    } satisfies OpenSettings;

    void browser.runtime.sendMessage(request).then(
      (response: unknown) => {
        const ack = parseAck(response);
        if (ack?.id === request.id) {
          status.textContent = browser.i18n.getMessage('settingsRequestSent');
        } else {
          void render();
        }
      },
      () => {
        void render();
      },
    );
  });
}
