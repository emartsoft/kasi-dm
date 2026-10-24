// M2.13c (docs/plans/m2-13.md, ADR 0026): the same browser and the same native host as scenarios.ts, but the far end of the pipe is the real
// Kasi.App, started by the host as Kasi.exe --tray. The harness only serves the test pages and Kasi.TestServer (no stub pipe). What counts here
// is what the real app did: the file it saved (by SHA-256), what the browser kept, and what it left in its data folder.
import { createHash } from 'node:crypto';
import { createReadStream, existsSync } from 'node:fs';
import { mkdtemp, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { waitFor, type BrowserDriver } from './support/driver';
import {
  installHost,
  kasiAppDirectory,
  startHarness,
  stopKasiIn,
  unavailableReason,
  type Harness,
  type HostSetup,
} from './support/harness';
import { registerHost } from './support/registration';
import type { BrowserKit } from './scenarios';

const CONNECTED = 'Connected to Kasi.';
const SESSION_COOKIE = 'kasi_session';

async function sha256File(path: string): Promise<string> {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path) as AsyncIterable<Buffer>) hash.update(chunk);
  return hash.digest('hex');
}

async function* filesUnder(dir: string): AsyncGenerator<string> {
  for (const name of await readdir(dir).catch(() => [] as string[])) {
    const path = join(dir, name);
    const info = await stat(path).catch(() => undefined);
    if (info?.isDirectory()) yield* filesUnder(path);
    else if (info) yield path;
  }
}

/** True when a file anywhere under `dir` has this SHA-256 (the app picks the name and the category folder). */
async function holdsFile(dir: string, sha256: string, size: number): Promise<boolean> {
  for await (const path of filesUnder(dir)) {
    if (path.endsWith('.part')) continue;
    if ((await stat(path)).size === size && (await sha256File(path)) === sha256) return true;
  }
  return false;
}

async function readText(path: string): Promise<string> {
  try {
    return (await readFile(path)).toString('latin1');
  } catch {
    return ''; // a file Kasi holds open and locked: the database, whose secrets are encrypted; the logs are plain files and can be read
  }
}

/** The extension erases the browser's own copy after Kasi accepts the download; the browser removes its partial file a moment later. */
async function browserHoldsNothing(driver: BrowserDriver): Promise<void> {
  await waitFor('the browser to hold no copy', async () => (await driver.folder()).length === 0);
}

export function realKasiUnavailable(): string | undefined {
  return existsSync(join(kasiAppDirectory(), 'Kasi.App.exe'))
    ? undefined
    : 'Kasi.App is not published (dotnet publish src/Kasi.App -c Release -o artifacts/kasi-app; or set KASI_APP_DIR)';
}

export function defineRealScenarios(kit: BrowserKit): void {
  const reason = unavailableReason() ?? realKasiUnavailable() ?? kit.unavailable();
  if (reason)
    console.warn(`end-to-end tests with the real Kasi (${kit.family}) are skipped: ${reason}`);
  const suite = describe.skipIf(reason !== undefined);
  // Firefox has finished a very small file before the extension can pause it (see scenarios.ts)
  const SIZE = kit.size ?? 5_000_000;

  suite(`end to end (${kit.family}): the real Kasi`, () => {
    let harness: Harness;
    let host: HostSetup;
    let restore: () => void;
    let driver: BrowserDriver;
    let dataDir: string;
    let downloadsDir: string;
    const saved: Record<string, string | undefined> = {};
    const variables = ['KASI_DATA_DIR', 'KASI_DOWNLOADS_DIR', 'KASI_ENDPOINT_SUFFIX'];
    const home = () => `${harness.admin}/pages/home?size=${String(SIZE)}`;

    beforeAll(async () => {
      harness = await startHarness({ stub: false });
      dataDir = await mkdtemp(join(tmpdir(), 'kasi-e2e-data-'));
      downloadsDir = await mkdtemp(join(tmpdir(), 'kasi-e2e-files-'));
      // The browser starts the native host and the host starts Kasi, and each inherits this process's environment: the real app keeps its
      // data and its downloads in these folders and listens on pipes of its own, so a Kasi the developer runs is never reached.
      for (const name of variables) saved[name] = process.env[name];
      process.env.KASI_DATA_DIR = dataDir;
      process.env.KASI_DOWNLOADS_DIR = downloadsDir;
      const suffix = `e2e${Math.random().toString(36).slice(2, 10)}`;
      process.env.KASI_ENDPOINT_SUFFIX = suffix;
      // Kasi asks before it adds what a browser hands over (the Add download window); a test has nobody to press Add, so it adds at once
      await writeFile(
        join(dataDir, `browser-settings-${suffix}.json`),
        JSON.stringify({ showAddDialog: false }),
      );
      host = await installHost(undefined, true);
      restore = registerHost(kit.family, await host.manifestPath(kit.family, kit.allowed));
      driver = await kit.launch();
      await waitFor(
        'the extension to reach the real Kasi',
        async () => (await driver.popupText()) === CONNECTED,
        90_000, // the host has to start Kasi first
      );
    });

    afterAll(async () => {
      await driver.close();
      restore();
      stopKasiIn(host.appDir);
      await harness.stop();
      await host.dispose();
      for (const name of variables) {
        if (saved[name] === undefined) Reflect.deleteProperty(process.env, name);
        else process.env[name] = saved[name];
      }
      await rm(dataDir, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
      await rm(downloadsDir, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
    });

    beforeEach(async () => {
      await driver.resetFiles();
      await driver.open(home());
    });

    it('[TC-BRW-001] the real Kasi saves a matching download, and the browser keeps no copy', async () => {
      const expected = await harness.sha256('plain', SIZE);
      await driver.click('#plain');

      await waitFor('Kasi to finish the file', () => holdsFile(downloadsDir, expected, SIZE));
      await browserHoldsNothing(driver);
    });

    it('[M2.12] a file behind a login downloads through the extension with the browser cookie, and no cookie is left behind', async () => {
      await driver.addCookie(SESSION_COOKIE, 'ok', harness.testServer);
      await driver.open(home()); // adding the cookie may have moved the browser to the download site
      const expected = await harness.sha256('secret', SIZE);
      await driver.click('#secret');

      await waitFor('Kasi to finish the login-protected file', () =>
        holdsFile(downloadsDir, expected, SIZE),
      );
      await browserHoldsNothing(driver);
      // the cookie rode in the job's secret, which is deleted when the job completes, and is never logged or saved in the clear
      for await (const path of filesUnder(dataDir)) {
        const text = await readText(path);
        expect(
          text.includes(SESSION_COOKIE),
          `${path.slice(dataDir.length)} holds the cookie name`,
        ).toBe(false);
      }
    });
  });
}
