import browser from 'webextension-polyfill';
import { ALL_SITES, renderStatus } from '../shared/connection-view';
import { readInterceptionEnabled, writeInterceptionEnabled } from '../shared/settings';

const title = document.querySelector<HTMLHeadingElement>('#title');
const status = document.querySelector<HTMLParagraphElement>('#status');
const allow = document.querySelector<HTMLButtonElement>('#allow-sites');
const toggle = document.querySelector<HTMLInputElement>('#interception');
const toggleLabel = document.querySelector<HTMLSpanElement>('#interception-label');

if (title && status && allow && toggle && toggleLabel) {
  toggleLabel.textContent = browser.i18n.getMessage('interceptionToggle');
  void readInterceptionEnabled(browser.storage.local).then((on) => {
    toggle.checked = on;
  });
  toggle.addEventListener('change', () => {
    void writeInterceptionEnabled(browser.storage.local, toggle.checked);
  });
  title.textContent = browser.i18n.getMessage('extensionName');
  allow.textContent = browser.i18n.getMessage('allowAllSites');
  allow.addEventListener('click', () => {
    // permissions.request needs this user gesture.
    const render = () => renderStatus(status, allow);
    void browser.permissions.request(ALL_SITES).then(render, render);
  });
  void renderStatus(status, allow);
  // The popup page is alive while it is open, so a timer is safe here (the worker's state is asked, never polled in the background).
  setInterval(() => {
    void renderStatus(status, allow);
  }, 1000);
}
