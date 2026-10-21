import browser from 'webextension-polyfill';
import { ALL_SITES, renderStatus } from '../shared/connection-view';

const title = document.querySelector<HTMLHeadingElement>('#title');
const status = document.querySelector<HTMLParagraphElement>('#status');
const allow = document.querySelector<HTMLButtonElement>('#allow-sites');

if (title && status && allow) {
  title.textContent = browser.i18n.getMessage('extensionName');
  allow.textContent = browser.i18n.getMessage('allowAllSites');
  allow.addEventListener('click', () => {
    // permissions.request needs this user gesture.
    const render = () => renderStatus(status, allow);
    void browser.permissions.request(ALL_SITES).then(render, render);
  });
  void renderStatus(status, allow);
}
