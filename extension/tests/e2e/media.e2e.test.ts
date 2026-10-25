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
import { FIREFOX_ADDON_ID, firefoxUnavailable, launchFirefox } from './support/firefox';

// KASI-MED-001 / TC-MED-001: each page makes one observable media request, then the extension reports it to Kasi.
const reason = unavailableReason();
const cases = [
  { kind: 'hls', path: '/hls/clear/master.m3u8' },
  { kind: 'dash', path: '/dash/clear/manifest.mpd' },
  { kind: 'direct', path: '/media/direct/2000000' },
] as const;

async function expectMedia(
  harness: Harness,
  driver: BrowserDriver,
  kind: (typeof cases)[number]['kind'],
  path: string,
): Promise<void> {
  await harness.clear();
  const pageUrl = `${harness.admin}/pages/media/${kind}`;
  await driver.open(pageUrl);
  const detected = await waitFor(`${kind} detection to reach Kasi`, async () =>
    (await harness.receivedMedia()).find(
      (item) => item.type === 'mediaDetected' && item.kind === kind,
    ),
  );
  expect(detected.url).toContain(path);
  expect(detected.referrer).toBe(pageUrl);
  const list = await waitFor(`${kind} popup entry`, async () => {
    const value = await driver.mediaText();
    return value.includes(kind === 'direct' ? 'Media' : kind.toUpperCase()) ? value : undefined;
  });
  expect(list).toContain(kind === 'direct' ? 'Media' : kind.toUpperCase());
  if (driver.family === 'chromium') {
    expect(await driver.mediaBadge()).toBe('1');
  }
  await driver.captureFirstMedia();
  const captured = await waitFor(`${kind} capture to reach Kasi`, async () =>
    (await harness.receivedMedia()).find(
      (item) => item.type === 'captureMedia' && item.kind === kind,
    ),
  );
  expect(captured.url).toBe(detected.url);
  expect(captured.referrer).toBe(pageUrl);
}

describe.skipIf(reason !== undefined)('end to end (chromium): media detection', () => {
  let extension: ChromiumExtension | undefined;
  let harness: Harness | undefined;
  let host: HostSetup | undefined;
  let restore: (() => void) | undefined;
  let driver: BrowserDriver | undefined;

  beforeAll(async () => {
    extension = await prepareChromiumExtension();
    harness = await startHarness();
    host = await installHost();
    restore = registerHost('chromium', await host.manifestPath('chromium', extension.id));
    driver = await launchChromium(extension);
  });

  afterAll(async () => {
    await driver?.close();
    restore?.();
    await host?.dispose();
    await harness?.stop();
    if (extension) await rm(extension.dir, { recursive: true, force: true });
  });

  for (const { kind, path } of cases) {
    it(`[TC-MED-001] detects and captures ${kind}`, async () => {
      if (!harness || !driver) throw new Error('media browser fixture did not start');
      await expectMedia(harness, driver, kind, path);
    });
  }
});

describe.skipIf(reason !== undefined || firefoxUnavailable() !== undefined)(
  'end to end (firefox): media detection',
  () => {
    let harness: Harness | undefined;
    let host: HostSetup | undefined;
    let restore: (() => void) | undefined;
    let driver: BrowserDriver | undefined;

    beforeAll(async () => {
      harness = await startHarness();
      host = await installHost();
      restore = registerHost('firefox', await host.manifestPath('firefox', FIREFOX_ADDON_ID));
      driver = await launchFirefox();
    });

    afterAll(async () => {
      await driver?.close();
      restore?.();
      await host?.dispose();
      await harness?.stop();
    });

    for (const { kind, path } of cases) {
      it(`[TC-MED-001] detects and captures ${kind}`, async () => {
        if (!harness || !driver) throw new Error('media browser fixture did not start');
        await expectMedia(harness, driver, kind, path);
      });
    }
  },
);
