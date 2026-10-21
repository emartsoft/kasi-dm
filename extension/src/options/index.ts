import browser from 'webextension-polyfill';
import { parseAck } from '../shared/messages';
import { STATUS_REQUEST, resolveStatus } from '../shared/status';
import type { OpenSettings } from '../types/protocol';

const ALL_SITES = { origins: ['<all_urls>'] };

const title = document.querySelector<HTMLHeadingElement>('#title');
const button = document.querySelector<HTMLButtonElement>('#open-settings');
const status = document.querySelector<HTMLParagraphElement>('#status');
const allow = document.querySelector<HTMLButtonElement>('#allow-sites');

async function isConnected(): Promise<boolean> {
  try {
    const reply: unknown = await browser.runtime.sendMessage(STATUS_REQUEST);
    return (
      typeof reply === 'object' &&
      reply !== null &&
      'connected' in reply &&
      reply.connected === true
    );
  } catch {
    return false;
  }
}

async function render(): Promise<void> {
  if (!status || !allow) return;
  const state = resolveStatus(await isConnected(), await browser.permissions.contains(ALL_SITES));
  status.textContent = browser.i18n.getMessage(`status_${state}`);
  allow.hidden = state !== 'needsPermission';
}

if (title && button && status && allow) {
  title.textContent = browser.i18n.getMessage('extensionName');
  button.textContent = browser.i18n.getMessage('openSettings');
  allow.textContent = browser.i18n.getMessage('allowAllSites');
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
