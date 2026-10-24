import { rm } from 'node:fs/promises';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { waitFor, type BrowserDriver } from './support/driver';
import {
  installHost,
  startHarness,
  unavailableReason,
  type Harness,
  type HostSetup,
} from './support/harness';
import {
  launchChromium,
  prepareChromiumExtension,
  type ChromiumExtension,
} from './support/chromium';
import { registerHost } from './support/registration';

// KASI-MED-001 / TC-MED-001 in a real Chromium: a page that fetches an HLS manifest is detected, appears in the popup and its Save
// button sends captureMedia through the native host to the stub Kasi. Firefox's media flow is covered by the unit suite for now:
// WebDriver cannot open the popup and click a row in the same way.
const reason = unavailableReason();
describe.skipIf(reason !== undefined)('end to end (chromium): media detection', () => {
  let extension: ChromiumExtension | undefined;
  let harness: Harness;
  let host: HostSetup;
  let restore: () => void;
  let driver: BrowserDriver;

  beforeAll(async () => {
    extension = await prepareChromiumExtension();
    harness = await startHarness();
    host = await installHost();
    restore = registerHost('chromium', await host.manifestPath('chromium', extension.id));
    driver = await launchChromium(extension);
  });

  afterAll(async () => {
    await driver.close();
    restore();
    await host.dispose();
    await harness.stop();
    if (extension) await rm(extension.dir, { recursive: true, force: true });
  });

  it('[TC-MED-001] detects an HLS manifest and forwards it to Kasi', async () => {
    await harness.clear();
    await driver.open(`${harness.admin}/pages/media/hls`);

    const detected = await waitFor('the HLS detection to reach Kasi', async () => {
      const media = await harness.receivedMedia();
      return media.find((item) => item.type === 'mediaDetected' && item.kind === 'hls');
    });
    expect(detected.url).toContain('/hls/clear/master.m3u8');

    expect(detected.referrer).toBe(`${harness.admin}/pages/media/hls`);
  });
});
