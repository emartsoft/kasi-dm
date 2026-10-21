import browser from 'webextension-polyfill';
import { STATUS_REQUEST, isConnectedReply, resolveStatus } from './status';

export const ALL_SITES = { origins: ['<all_urls>'] };

async function isConnected(): Promise<boolean> {
  try {
    return isConnectedReply(await browser.runtime.sendMessage(STATUS_REQUEST));
  } catch {
    return false;
  }
}

// Shows the connection state in the page and offers the all-sites permission button when it is missing.
export async function renderStatus(status: HTMLElement, allow: HTMLButtonElement): Promise<void> {
  const state = resolveStatus(await isConnected(), await browser.permissions.contains(ALL_SITES));
  status.textContent = browser.i18n.getMessage(`status_${state}`);
  allow.hidden = state !== 'needsPermission';
}
