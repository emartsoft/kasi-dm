// Firefox driven through geckodriver (support/webdriver.ts). What cannot be done this way, and is therefore not asserted here:
// the toolbar badge text (WebDriver cannot read a browser action's badge, so badge() is undefined and the popup carries the proof),
// the revoked-permission case (a WebDriver-installed add-on cannot have its host permission taken away), and private windows.
import { randomUUID } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { emptyDirectory, filesIn, type BrowserDriver } from './driver';
import { findGeckodriver, WebDriverSession } from './webdriver';

/** The add-on id in manifest.firefox.json (store-ids.json); the host manifest allows exactly this one. */
export const FIREFOX_ADDON_ID = (
  JSON.parse(readFileSync(new URL('../../../store-ids.json', import.meta.url), 'utf8')) as {
    firefox: string[];
  }
).firefox[0] as string;

const dist = fileURLToPath(new URL('../../../dist/firefox', import.meta.url));
const defaultBinary = 'C:\\Program Files\\Mozilla Firefox\\firefox.exe';

export function firefoxUnavailable(): string | undefined {
  if (!findGeckodriver()) return 'geckodriver was not found (put it on PATH or set GECKODRIVER)';
  if (!existsSync(dist)) return 'extension/dist/firefox is not built (npm run build)';
  return undefined;
}

export interface FirefoxAddon {
  /** The unpacked add-on folder. */
  dir: string;
  id: string;
}

/** `addon` defaults to dist/firefox; TC-BRW-007 passes a copy with a different gecko id. */
export async function launchFirefox(
  profile?: string,
  addon: FirefoxAddon = { dir: dist, id: FIREFOX_ADDON_ID },
): Promise<BrowserDriver> {
  const geckodriver = findGeckodriver();
  if (!geckodriver) throw new Error('geckodriver was not found');
  const downloads = await mkdtemp(join(tmpdir(), 'kasi-downloads-'));
  const uuid = randomUUID(); // a fixed internal id gives the popup a known moz-extension:// address
  const binary = process.env.FIREFOX ?? (existsSync(defaultBinary) ? defaultBinary : undefined);
  const session = await WebDriverSession.start(geckodriver, {
    ...(binary ? { binary } : {}),
    args: ['-headless', ...(profile ? ['-profile', profile] : [])],
    prefs: {
      'browser.download.folderList': 2,
      'browser.download.dir': downloads,
      'browser.download.useDownloadDir': true,
      'browser.download.always_ask_before_handling_new_types': false,
      'browser.helperApps.neverAsk.saveToDisk': 'application/octet-stream',
      // a second host name for the exclusion case; it resolves to loopback and never leaves the machine
      'network.dns.localDomains': 'excluded.test',
      'extensions.webextensions.uuids': JSON.stringify({ [addon.id]: uuid }),
    },
  });
  await session.installAddon(addon.dir);

  async function activatePageTab(pageUrl: string): Promise<void> {
    const error = await session.executeAsync<string | null>(
      `const [pageUrl, done] = arguments;
       browser.tabs.query({}).then((tabs) => {
         const tab = tabs.find((item) => item.url === pageUrl);
         if (!tab || tab.id === undefined) throw new Error('the media page tab was not found');
         return browser.tabs.update(tab.id, { active: true });
       }).then(() => done(null), (error) => done(String(error)));`,
      [pageUrl],
    );
    if (error) throw new Error(error);
  }

  return {
    family: 'firefox',
    open: (url) => session.navigate(url),
    click: (selector, modifiers) => session.click(selector, modifiers),
    addCookie: async (name, value, siteUrl) => {
      // WebDriver sets a cookie for the current page's site only
      await session.navigate(`${siteUrl}/`);
      await session.addCookie(name, value);
    },
    files: async () => (await filesIn(downloads)).filter((f) => !f.name.endsWith('.part')),
    folder: async () => (await filesIn(downloads)).map((f) => f.name),
    resetFiles: () => emptyDirectory(downloads),
    selectText: async (selector) => {
      await session.execute(
        `const element = document.querySelector(arguments[0]);
         if (!element) throw new Error('nothing to select');
         const range = document.createRange();
         range.selectNodeContents(element);
         const selection = window.getSelection();
         selection.removeAllRanges();
         selection.addRange(range);`,
        [selector],
      );
    },
    menuClick: async (menuItemId, info = {}) => {
      const pageUrl = await session.execute<string>('return document.URL;');
      await session.inExtensionTab(`moz-extension://${uuid}/src/options/index.html`, async () => {
        await session.executeAsync(
          `const [menuItemId, pageUrl, info, done] = arguments;
           browser.tabs.query({}).then((tabs) => {
             const tab = tabs.find((t) => t.url === pageUrl);
             if (!tab) throw new Error('the page tab was not found');
             return browser.runtime.sendMessage({
               kind: 'kasi-menu-click',
               info: Object.assign({ menuItemId, pageUrl }, info),
               tab: { id: tab.id, url: pageUrl },
             });
           }).then(() => done(null), (error) => done(String(error)));`,
          [menuItemId, pageUrl, info],
        );
      });
    },
    popupText: () =>
      session.inExtensionTab(`moz-extension://${uuid}/src/popup/index.html`, async () => {
        for (let attempt = 0; attempt < 50; attempt++) {
          const text = await session.text('#status');
          if (text !== '') return text;
          await new Promise((done) => setTimeout(done, 100));
        }
        return '';
      }),
    mediaText: async () => {
      const pageUrl = await session.execute<string>('return document.URL;');
      return session.inExtensionTab(`moz-extension://${uuid}/src/popup/index.html`, async () => {
        await activatePageTab(pageUrl);
        for (let attempt = 0; attempt < 50; attempt++) {
          const text = await session.text('#media-list');
          if (text !== '') return text;
          await new Promise((done) => setTimeout(done, 100));
        }
        return '';
      });
    },
    captureFirstMedia: async () => {
      const pageUrl = await session.execute<string>('return document.URL;');
      return session.inExtensionTab(`moz-extension://${uuid}/src/popup/index.html`, async () => {
        await activatePageTab(pageUrl);
        for (let attempt = 0; attempt < 50; attempt++) {
          if (await session.text('#media-list')) break;
          await new Promise((done) => setTimeout(done, 100));
        }
        await session.click('#media-list li button');
      });
    },
    badge: () => Promise.resolve(undefined),
    mediaBadge: () => Promise.resolve(undefined),
    close: async () => {
      await session.quit();
      await rm(downloads, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
    },
  };
}
