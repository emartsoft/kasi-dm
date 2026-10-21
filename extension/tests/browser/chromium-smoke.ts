// Nightly smoke (not PR CI): loads dist/chromium in a real Chromium and checks that the popup renders the
// "not connected" state. No native host is registered here, so connectNative fails and the manager keeps retrying.
// Run `npm run build:chromium` and `npx playwright install chromium` first. No network access is needed.
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const extension = fileURLToPath(new URL('../../dist/chromium', import.meta.url));
const profile = await mkdtemp(join(tmpdir(), 'kasi-smoke-'));

const context = await chromium.launchPersistentContext(profile, {
  channel: 'chromium', // the new headless mode, which supports extensions
  args: [`--disable-extensions-except=${extension}`, `--load-extension=${extension}`],
});

try {
  const worker = context.serviceWorkers()[0] ?? (await context.waitForEvent('serviceworker'));
  const id = new URL(worker.url()).host;

  const popup = await context.newPage();
  await popup.goto(`chrome-extension://${id}/src/popup/index.html`);
  await popup.getByText('Kasi is not connected.').waitFor({ timeout: 10_000 });
  console.log('Chromium smoke passed: the popup shows the not-connected state.');
} finally {
  await context.close();
  await rm(profile, { recursive: true, force: true });
}
