// Playwright's Chromium with the unpacked extension in a persistent context (ADR 0019: Playwright's download handling still lets
// downloads.onCreated fire, and an extension cancel shows up as a cancelled download). Branded Chrome and Edge ignore --load-extension.
import { createHash, generateKeyPairSync } from 'node:crypto';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, type BrowserContext, type Download, type Worker } from 'playwright';
import {
  emptyDirectory,
  filesIn,
  type BrowserDriver,
  type Modifier,
  type SavedFile,
} from './driver';

const dist = fileURLToPath(new URL('../../../dist/chromium', import.meta.url));

export interface ChromiumExtension {
  /** The unpacked extension with a per-run public key, so its id is known before the browser starts. */
  dir: string;
  id: string;
}

/** Copies dist/chromium and adds a freshly generated manifest "key"; the host manifest allows exactly the id derived from it. */
export async function prepareChromiumExtension(): Promise<ChromiumExtension> {
  const der = generateKeyPairSync('rsa', { modulusLength: 2048 }).publicKey.export({
    type: 'spki',
    format: 'der',
  });
  const id = [...createHash('sha256').update(der).digest().subarray(0, 16)]
    .map((b) => String.fromCharCode(97 + (b >> 4)) + String.fromCharCode(97 + (b & 15)))
    .join('');
  const dir = await mkdtemp(join(tmpdir(), 'kasi-ext-'));
  await cp(dist, dir, { recursive: true });
  const path = join(dir, 'manifest.json');
  const manifest = JSON.parse(await readFile(path, 'utf8')) as Record<string, unknown>;
  manifest.key = der.toString('base64');
  await writeFile(path, JSON.stringify(manifest));
  return { dir, id };
}

async function filesFor(download: Download): Promise<SavedFile[]> {
  const path = await download.path();
  return [
    {
      name: download.suggestedFilename(),
      sha256: createHash('sha256')
        .update(await readFile(path))
        .digest('hex'),
    },
  ];
}

/** `profile` keeps the browser's profile (and the extension's cached rules) across launches; the caller then removes it. */
export async function launchChromium(
  extension: ChromiumExtension,
  profile?: string,
): Promise<BrowserDriver> {
  const userData = profile ?? (await mkdtemp(join(tmpdir(), 'kasi-chromium-')));
  const downloads = await mkdtemp(join(tmpdir(), 'kasi-downloads-'));
  const context: BrowserContext = await chromium.launchPersistentContext(userData, {
    channel: 'chromium', // the new headless mode, which supports extensions
    downloadsPath: downloads,
    args: [
      `--disable-extensions-except=${extension.dir}`,
      `--load-extension=${extension.dir}`,
      // a second host name for the exclusion case; it resolves to loopback and never leaves the machine
      '--host-resolver-rules=MAP excluded.test 127.0.0.1',
    ],
  });
  const worker: Worker =
    context.serviceWorkers()[0] ?? (await context.waitForEvent('serviceworker'));
  const page = await context.newPage();

  // Chromium writes the file under a new name from the first byte, so a paused or cancelled download leaves bytes in the folder.
  // Only a download Playwright reports as completed counts as finished.
  let finished: SavedFile[] = [];
  context.on('download', (download) => {
    void (async () => {
      if ((await download.failure()) !== null) return;
      const [saved] = await filesFor(download);
      if (saved) finished.push(saved);
    })();
  });

  return {
    family: 'chromium',
    open: async (url) => {
      await page.goto(url);
    },
    click: async (selector, modifiers?: Modifier[]) => {
      await page.click(selector, modifiers ? { modifiers } : {});
    },
    addCookie: async (name, value, siteUrl) => {
      await context.addCookies([{ name, value, url: siteUrl }]);
    },
    files: () => Promise.resolve([...finished]),
    folder: async () => (await filesIn(downloads)).map((f) => f.name),
    resetFiles: async () => {
      finished = [];
      await emptyDirectory(downloads);
    },
    popupText: async () => {
      const popup = await context.newPage();
      try {
        await popup.goto(`chrome-extension://${extension.id}/src/popup/index.html`);
        const status = popup.locator('#status');
        await popup.waitForFunction(() => document.querySelector('#status')?.textContent !== '');
        return (await status.textContent()) ?? '';
      } finally {
        await popup.close();
      }
    },
    badge: () =>
      worker.evaluate(() =>
        (
          globalThis as unknown as {
            chrome: { action: { getBadgeText(d: object): Promise<string> } };
          }
        ).chrome.action.getBadgeText({}),
      ),
    close: async () => {
      await context.close();
      if (!profile) await rm(userData, { recursive: true, force: true });
      await rm(downloads, { recursive: true, force: true });
    },
  };
}
