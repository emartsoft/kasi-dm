import browser from 'webextension-polyfill';
import { STATUS_REQUEST, resolveStatus } from '../shared/status';

const ALL_SITES = { origins: ['<all_urls>'] };

const title = document.querySelector<HTMLHeadingElement>('#title');
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

if (title && status && allow) {
  title.textContent = browser.i18n.getMessage('extensionName');
  allow.textContent = browser.i18n.getMessage('allowAllSites');
  allow.addEventListener('click', () => {
    // permissions.request needs this user gesture.
    void browser.permissions.request(ALL_SITES).then(render, render);
  });
  void render();
}
