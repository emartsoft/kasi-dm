import browser from 'webextension-polyfill';
import { ALL_SITES, renderStatus } from '../shared/connection-view';
import { kindLabel } from '../shared/media-controller';
import { readInterceptionEnabled, writeInterceptionEnabled } from '../shared/settings';

const title = document.querySelector<HTMLHeadingElement>('#title');
const status = document.querySelector<HTMLParagraphElement>('#status');
const allow = document.querySelector<HTMLButtonElement>('#allow-sites');
const toggle = document.querySelector<HTMLInputElement>('#interception');
const toggleLabel = document.querySelector<HTMLSpanElement>('#interception-label');
const mediaSection = document.querySelector<HTMLElement>('#media-section');
const mediaTitle = document.querySelector<HTMLHeadingElement>('#media-title');
const mediaList = document.querySelector<HTMLUListElement>('#media-list');
const mediaStatus = document.querySelector<HTMLParagraphElement>('#media-status');

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
    void renderMedia();
  }, 1000);
  void renderMedia();
}

interface MediaReply {
  ok?: boolean;
  media?: { kind: 'hls' | 'dash' | 'direct'; url: string; title?: string }[];
  code?: string;
}

async function renderMedia(): Promise<void> {
  if (!mediaSection || !mediaTitle || !mediaList || !mediaStatus) return;
  const tabs = await browser.tabs.query({ active: true, currentWindow: true });
  const tabId = tabs[0]?.id;
  if (tabId === undefined) return;
  let reply: MediaReply;
  try {
    reply = await browser.runtime.sendMessage<{ type: string; tabId: number }, MediaReply>({
      type: 'mediaList',
      tabId,
    });
  } catch {
    return;
  }
  const media = reply.media ?? [];
  mediaSection.hidden = media.length === 0;
  if (media.length === 0) return;
  mediaTitle.textContent = browser.i18n.getMessage('media_title');
  mediaList.replaceChildren(
    ...media.map((item) => {
      const row = document.createElement('li');
      const text = document.createElement('span');
      text.textContent = `${kindLabel(item.kind)} — ${item.title ?? item.url}`;
      const save = document.createElement('button');
      save.type = 'button';
      save.textContent = browser.i18n.getMessage('media_save');
      save.addEventListener('click', () => {
        save.disabled = true;
        mediaStatus.textContent = browser.i18n.getMessage('media_capturing');
        void browser.runtime
          .sendMessage({ type: 'mediaCapture', tabId, url: item.url })
          .then((result: unknown) => {
            const answer = result as MediaReply;
            mediaStatus.textContent = answer.ok
              ? browser.i18n.getMessage('media_sent')
              : browser.i18n.getMessage('media_failed');
          })
          .catch(() => {
            mediaStatus.textContent = browser.i18n.getMessage('media_failed');
          });
      });
      row.append(text, save);
      return row;
    }),
  );
}
