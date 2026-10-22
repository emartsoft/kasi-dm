// TC-BRW-007 end to end (KASI-NFR-010): the browser refuses an extension that the host manifest does not allow-list. The host is
// registered for ONE extension id; a copy of the extension with a different id is then loaded and must not reach Kasi. The assertions
// are on effects only (no `hello` at the stub Kasi, no host process, a closed port): the wording of the refusal differs per browser.
// Each family first runs the allowed copy as a control, so a quiet rogue copy cannot be a broken harness.
import { spawnSync } from 'node:child_process';
import { cp, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import {
  launchChromium,
  prepareChromiumExtension,
  type ChromiumExtension,
} from './support/chromium';
import { pause, waitFor, type BrowserDriver } from './support/driver';
import { FIREFOX_ADDON_ID, firefoxUnavailable, launchFirefox } from './support/firefox';
import {
  installHost,
  startHarness,
  unavailableReason,
  type Harness,
  type HostSetup,
} from './support/harness';
import { registerHost, type Family } from './support/registration';

const CONNECTED = 'Connected to Kasi.';
const NOT_CONNECTED = 'Kasi is not connected.';
// a refusal gives no event to wait for; this is long enough for the control to have connected many times over
const QUIET_MS = 8_000;

/** How many Kasi.NativeHost.exe processes run from `appDir` (the temp copy the browser would start). */
function hostProcesses(appDir: string): number {
  const query =
    `@(Get-CimInstance Win32_Process -Filter "Name='Kasi.NativeHost.exe'" | ` +
    `Where-Object { $_.ExecutablePath -like '${appDir}\\*' }).Count`;
  const result = spawnSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', query], {
    encoding: 'utf8',
    windowsHide: true,
  });
  if (result.status !== 0) throw new Error(`could not list processes: ${result.stderr}`);
  return Number(result.stdout.trim());
}

interface Kit {
  family: Family;
  unavailable(): string | undefined;
  /** Makes the allowed and the rogue copy; returns the allowed id and the launchers, and a cleanup. */
  prepare(): Promise<{
    allowedId: string;
    launchAllowed(): Promise<BrowserDriver>;
    launchRogue(): Promise<BrowserDriver>;
    cleanup(): Promise<void>;
  }>;
}

function defineAllowList(kit: Kit): void {
  const reason = unavailableReason() ?? kit.unavailable();
  if (reason) console.warn(`TC-BRW-007 (${kit.family}) is skipped: ${reason}`);

  describe.skipIf(reason !== undefined)(`[TC-BRW-007] allow-list (${kit.family})`, () => {
    let harness: Harness;
    let host: HostSetup;
    let restore: () => void;
    let prepared: Awaited<ReturnType<Kit['prepare']>>;

    beforeAll(async () => {
      prepared = await kit.prepare();
      harness = await startHarness();
      host = await installHost();
      restore = registerHost(kit.family, await host.manifestPath(kit.family, prepared.allowedId));
    });

    afterAll(async () => {
      restore();
      await harness.stop();
      await host.dispose();
      await prepared.cleanup();
    });

    it('control: the allow-listed extension connects, its hello reaches Kasi and the browser starts the host', async () => {
      const driver = await prepared.launchAllowed();
      try {
        await waitFor(
          'the extension to reach Kasi',
          async () => (await driver.popupText()) === CONNECTED,
        );
        expect(await harness.hellos()).toBeGreaterThan(0);
        expect(hostProcesses(host.appDir)).toBeGreaterThan(0);
      } finally {
        await driver.close();
      }
      await waitFor('the host to exit with the browser', () =>
        Promise.resolve(hostProcesses(host.appDir) === 0),
      );
    });

    it('an extension that is not allow-listed never reaches the host or Kasi', async () => {
      const hellosBefore = await harness.hellos();
      const driver = await prepared.launchRogue();
      try {
        await pause(QUIET_MS);
        expect(await driver.popupText()).toBe(NOT_CONNECTED);
        expect(await harness.hellos()).toBe(hellosBefore);
        expect(hostProcesses(host.appDir)).toBe(0);
      } finally {
        await driver.close();
      }
    });
  });
}

defineAllowList({
  family: 'chromium',
  unavailable: () => undefined,
  prepare: async () => {
    // two copies with different generated keys, so two different ids; the host manifest will allow only the first
    const allowed: ChromiumExtension = await prepareChromiumExtension();
    const rogue: ChromiumExtension = await prepareChromiumExtension();
    return {
      allowedId: allowed.id,
      launchAllowed: () => launchChromium(allowed),
      launchRogue: () => launchChromium(rogue),
      cleanup: async () => {
        await rm(allowed.dir, { recursive: true, force: true });
        await rm(rogue.dir, { recursive: true, force: true });
      },
    };
  },
});

const firefoxDist = fileURLToPath(new URL('../../dist/firefox', import.meta.url));
const ROGUE_ADDON_ID = 'rogue@martsoft.co.ke';

defineAllowList({
  family: 'firefox',
  unavailable: firefoxUnavailable,
  prepare: async () => {
    // a copy of the add-on under a different gecko id; the host manifest allows only the real one
    const dir = await mkdtemp(join(tmpdir(), 'kasi-rogue-addon-'));
    await cp(firefoxDist, dir, { recursive: true });
    const path = join(dir, 'manifest.json');
    const manifest = JSON.parse(await readFile(path, 'utf8')) as {
      browser_specific_settings: { gecko: { id: string } };
    };
    manifest.browser_specific_settings.gecko.id = ROGUE_ADDON_ID;
    await writeFile(path, JSON.stringify(manifest));
    return {
      allowedId: FIREFOX_ADDON_ID,
      launchAllowed: () => launchFirefox(),
      launchRogue: () => launchFirefox(undefined, { dir, id: ROGUE_ADDON_ID }),
      cleanup: () => rm(dir, { recursive: true, force: true }),
    };
  },
});
