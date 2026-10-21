import browser from 'webextension-polyfill';

const title = document.querySelector<HTMLHeadingElement>('#title');
const status = document.querySelector<HTMLParagraphElement>('#status');

if (title && status) {
  title.textContent = browser.i18n.getMessage('extensionName');
  status.textContent = browser.i18n.getMessage('connectionUnavailable');
}
