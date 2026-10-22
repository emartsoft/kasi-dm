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
    popupText: () =>
      session.inExtensionTab(`moz-extension://${uuid}/src/popup/index.html`, async () => {
        for (let attempt = 0; attempt < 50; attempt++) {
          const text = await session.text('#status');
          if (text !== '') return text;
          await new Promise((done) => setTimeout(done, 100));
        }
        return '';
      }),
    badge: () => Promise.resolve(undefined),
    close: async () => {
      await session.quit();
      await rm(downloads, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
    },
  };
}
