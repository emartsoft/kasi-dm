// Generates the store screenshots (1280x800) and promo tiles (440x280, 1400x560) into docs/store (plan m2-7 2.1, decision 5): the popup and
// options pages are photographed in Chromium while the stub Kasi is running, so the pictures show the real extension in its real states.
// Not a check: it only runs with KASI_E2E=1 and KASI_STORE_ASSETS=1, and rewrites the committed PNGs. Run it after the UI text changes.
import { readFile, mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium, type Browser, type Page } from 'playwright';
import { afterAll, beforeAll, describe, it } from 'vitest';
import { waitFor } from './support/driver';
import {
  installHost,
  startHarness,
  unavailableReason,
  type Harness,
  type HostSetup,
} from './support/harness';
import { prepareChromiumExtension, type ChromiumExtension } from './support/chromium';
import { registerHost } from './support/registration';

const repo = fileURLToPath(new URL('../../../', import.meta.url));
const docs = resolve(repo, 'docs/store');
const reason =
  unavailableReason() ??
  (process.env.KASI_STORE_ASSETS === '1' ? undefined : 'KASI_STORE_ASSETS=1 is not set');

const BLUE = '#0067c0';

async function dataUri(path: string): Promise<string> {
  return `data:image/png;base64,${(await readFile(path)).toString('base64')}`;
}

/** A page of fixed size made from HTML, photographed. */
async function render(page: Page, width: number, height: number, html: string, file: string) {
  await page.setViewportSize({ width, height });
  await page.setContent(`<!doctype html><meta charset="utf-8"><style>
    *{box-sizing:border-box;margin:0}
    body{width:${String(width)}px;height:${String(height)}px;font-family:Segoe UI,Arial,sans-serif;color:#fff;
      background:linear-gradient(135deg,${BLUE},#0a2f5c);overflow:hidden}
  </style>${html}`);
  await mkdir(resolve(file, '..'), { recursive: true });
  await page.screenshot({ path: file });
}

describe.skipIf(reason !== undefined)('store assets', () => {
  let harness: Harness;
  let host: HostSetup;
  let extension: ChromiumExtension;
  let restore: () => void;
  let profile: string;

  beforeAll(async () => {
    harness = await startHarness();
    host = await installHost();
    extension = await prepareChromiumExtension();
    restore = registerHost('chromium', await host.manifestPath('chromium', extension.id));
    profile = await mkdtemp(join(tmpdir(), 'kasi-shots-'));
  });

  afterAll(async () => {
    restore();
    await harness.stop();
    await host.dispose();
    await rm(extension.dir, { recursive: true, force: true });
    await rm(profile, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
  });

  it('writes the screenshots and the promo tiles', async () => {
    let composer: Browser | undefined;
    const context = await chromium.launchPersistentContext(profile, {
      channel: 'chromium',
      deviceScaleFactor: 2,
      args: [`--disable-extensions-except=${extension.dir}`, `--load-extension=${extension.dir}`],
    });
    try {
      const popup = await context.newPage();
      const base = `chrome-extension://${extension.id}/src`;
      await popup.setViewportSize({ width: 340, height: 170 });
      await popup.goto(`${base}/popup/index.html`);
      await waitFor(
        'the popup to say connected',
        async () => (await popup.textContent('#status')) === 'Connected to Kasi.',
      );
      const connected = (await popup.screenshot()).toString('base64');

      await harness.stopPipe();
      await waitFor(
        'the popup to say not connected',
        async () => (await popup.textContent('#status')) === 'Kasi is not connected.',
      );
      const disconnected = (await popup.screenshot()).toString('base64');

      const options = await context.newPage();
      await options.setViewportSize({ width: 900, height: 240 });
      await options.goto(`${base}/options/index.html`);
      const optionsShot = (await options.screenshot()).toString('base64');

      // composed at 1x: the stores want exactly 1280x800 and 1400x560 pixels, while the pictures inside are photographed at 2x
      composer = await chromium.launch();
      const page = await (await composer.newContext({ deviceScaleFactor: 1 })).newPage();
      const icon = await dataUri(resolve(repo, 'extension/public/icons/128.png'));
      const frame = (image: string, caption: string, width: number) => `
        <div style="display:flex;flex-direction:column;align-items:center;justify-content:center;height:100%;gap:36px;padding:48px">
          <div style="font-size:44px;font-weight:600;text-align:center">${caption}</div>
          <img src="data:image/png;base64,${image}" style="width:${String(width)}px;border-radius:12px;box-shadow:0 12px 40px #0008;background:#fff">
        </div>`;
      const shots = resolve(docs, 'screenshots');
      await render(
        page,
        1280,
        800,
        frame(connected, 'Downloads go straight to Kasi', 680),
        resolve(shots, '1-popup-connected.png'),
      );
      await render(
        page,
        1280,
        800,
        frame(disconnected, 'If Kasi is not running, your browser carries on', 680),
        resolve(shots, '2-popup-not-connected.png'),
      );
      await render(
        page,
        1280,
        800,
        frame(optionsShot, 'Simple settings, no accounts, no tracking', 1000),
        resolve(shots, '3-options.png'),
      );

      const tile = (width: number, height: number, mark: number, title: number, sub: number) => `
        <div style="display:flex;align-items:center;justify-content:center;gap:${String(Math.round(width / 20))}px;height:100%;padding:${String(Math.round(width / 16))}px">
          <img src="${icon}" style="width:${String(mark)}px;height:${String(mark)}px">
          <div><div style="font-size:${String(title)}px;font-weight:700">Kasi</div>
          <div style="font-size:${String(sub)}px;opacity:.9">Send downloads to Kasi,<br>the download manager for Windows</div></div>
        </div>`;
      const assets = resolve(docs, 'assets');
      await render(
        page,
        440,
        280,
        tile(440, 280, 96, 52, 17),
        resolve(assets, 'promo-small-440x280.png'),
      );
      await render(
        page,
        1400,
        560,
        tile(1400, 560, 220, 120, 40),
        resolve(assets, 'promo-marquee-1400x560.png'),
      );
      await writeFile(
        resolve(assets, 'README.md'),
        'Generated by `extension/tests/e2e/store-assets.e2e.test.ts` (KASI_E2E=1 KASI_STORE_ASSETS=1 npm run test:e2e). Do not edit by hand.\n',
      );
    } finally {
      await composer?.close();
      await context.close();
    }
  }, 120_000);
});
