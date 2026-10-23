// TC-BRW-001 to 004 end to end, the same assertions for every browser (plan 3.2): what the stub Kasi received, the files the browser
// kept (by SHA-256) and the popup and badge text. Only the BrowserDriver differs. Waits are generous polls; nothing asserts a duration.
import { mkdtemp, rename, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { afterAll, beforeAll, beforeEach, describe, expect, it } from 'vitest';
import { pause, sha256Of, waitFor, type BrowserDriver, type Modifier } from './support/driver';
import {
  attach,
  freePort,
  installHost,
  startHarness,
  stopTray,
  unavailableReason,
  type Harness,
  type HostSetup,
} from './support/harness';
import { registerHost, type Family } from './support/registration';

export interface BrowserKit {
  family: Family;
  /** What the host manifest allows: the Chromium extension id or the Firefox add-on id. */
  allowed: string;
  /** With a profile folder the profile survives `close()`, so a second launch finds what the extension cached. */
  launch(profile?: string): Promise<BrowserDriver>;
  /** Size of the files the scenarios download; large enough that the browser is still receiving when the extension pauses it. */
  size?: number;
  /** False when the browser drops a temporary add-on's storage between sessions, so a restart loses the cached rules. */
  rulesSurviveRestart?: boolean;
  /** Why this browser cannot be driven here, if it cannot. */
  unavailable(): string | undefined;
}

// Set per browser by defineScenarios: Firefox has finished a very small file before the extension can pause it.
let SIZE = 20_000;
const CANARY_NAME = 'kasi_e2e_canary';
const CANARY_VALUE = 'CANARY-COOKIE-VALUE-5d1e';
const NOT_CONNECTED = 'Kasi is not connected.';
const CONNECTED = 'Connected to Kasi.';
const home = (h: { admin: string }) => `${h.admin}/pages/home?size=${String(SIZE)}`;

async function fileSha(
  seed: string,
  harness: { sha256(seed: string, size: number): Promise<string> },
) {
  return harness.sha256(seed, SIZE);
}

export function defineScenarios(kit: BrowserKit): void {
  SIZE = kit.size ?? 20_000;
  const reason = unavailableReason() ?? kit.unavailable();
  if (reason) console.warn(`end-to-end tests (${kit.family}) are skipped: ${reason}`);
  const suite = describe.skipIf(reason !== undefined);

  suite(`end to end (${kit.family}): Kasi running`, () => {
    let harness: Harness;
    let host: HostSetup;
    let restore: () => void;
    let driver: BrowserDriver;

    beforeAll(async () => {
      harness = await startHarness();
      host = await installHost();
      restore = registerHost(kit.family, await host.manifestPath(kit.family, kit.allowed));
      driver = await kit.launch();
      await waitFor(
        'the extension to reach Kasi',
        async () => (await driver.popupText()) === CONNECTED,
      );
    });

    afterAll(async () => {
      await driver.close();
      restore();
      await harness.stop();
      await host.dispose();
    });

    beforeEach(async () => {
      await harness.clear();
      await harness.setMode('ack');
      await harness.setRules({}); // the defaults, and a rules message clears a leftover badge
      await driver.resetFiles();
      await driver.open(home(harness));
    });

    const handedOff = () =>
      waitFor('the hand-off', async () => {
        const all = await harness.received();
        return all.length > 0 ? all : false;
      });

    it('[TC-BRW-001] hands a matching download to Kasi with its referrer and cookies, and erases the browser copy', async () => {
      await driver.addCookie(CANARY_NAME, CANARY_VALUE, harness.testServer);
      await driver.open(home(harness)); // adding the cookie may have moved the browser to the download site
      await driver.click('#plain');

      const [first, ...rest] = await handedOff();
      expect(rest).toHaveLength(0);
      expect(first?.url).toBe(`${harness.testServer}/files/plain/${String(SIZE)}`);
      expect(first?.finalUrl).toBe(first?.url);
      // Chromium reports the referrer's origin, Firefox the whole page address
      expect(first?.referrer?.startsWith(`${harness.admin}/`)).toBe(true);
      expect(first?.mime).toBe('application/octet-stream');
      expect(first?.cookies).toContainEqual(
        expect.objectContaining({ name: CANARY_NAME, value: CANARY_VALUE }),
      );

      await pause(1500); // the browser's own copy must not appear after the ack
      // a paused download still completes when all its bytes were in; what counts is that the extension removed the file
      expect(await driver.folder()).toEqual([]);
    });

    it.each(['silent', 'error'] as const)(
      '[TC-BRW-002] Kasi answers %s: the browser keeps and finishes the download, and the badge shows a failure',
      async (mode) => {
        await harness.setMode(mode);
        await driver.click('#plain');

        await handedOff();
        const expected = await fileSha('plain', harness);
        await waitFor('the browser to finish the file', async () =>
          (await driver.files()).some((f) => f.sha256 === expected),
        );
        if ((await driver.badge()) !== undefined) {
          await waitFor('the failure badge', async () => (await driver.badge()) === '!');
        }
      },
    );

    // Alt-click is the browser's own "download this link" on Windows, which is why it is the default key; Ctrl and Shift change what
    // the browser does with a link, so they are tried on a button whose script starts the download (key detection alone).
    const bypasses: [string, Modifier, 'alt' | 'ctrl' | 'shift', string, string][] = [
      ['Alt on a link', 'Alt', 'alt', '#plain', 'plain'],
      ['Ctrl on a button', 'Control', 'ctrl', '#scripted', 'scripted'],
      ['Shift on a button', 'Shift', 'shift', '#scripted', 'scripted'],
    ];
    it.each(bypasses)(
      '[TC-BRW-003] %s bypasses Kasi: the browser keeps the download and Kasi hears nothing',
      async (_name, modifier, bypassKey, link, seed) => {
        await harness.setRules({ bypassKey });
        await driver.click(link, [modifier]);

        const expected = await fileSha(seed, harness);
        await waitFor('the browser to finish the file', async () =>
          (await driver.files()).some((f) => f.sha256 === expected),
        );
        expect(await harness.received()).toEqual([]);
      },
    );

    it('[TC-BRW-003] Alt still bypasses when the server answers only after three seconds', async () => {
      await driver.click('#slow', ['Alt']);

      const expected = await fileSha('slow', harness);
      await waitFor('the browser to finish the file', async () =>
        (await driver.files()).some((f) => f.sha256 === expected),
      );
      expect(await harness.received()).toEqual([]);
    });

    it('[TC-BRW-002] Kasi quitting turns the popup to not connected and the badge to !; the next download reconnects', async () => {
      // the pipe closes as it does when the user quits Kasi: the host tells the extension once and exits, and nothing reconnects by itself
      await harness.stopPipe();
      await waitFor(
        'the popup to say not connected',
        async () => (await driver.popupText()) === NOT_CONNECTED,
        15_000,
      );
      if ((await driver.badge()) !== undefined) {
        await waitFor('the failure badge', async () => (await driver.badge()) === '!');
      }

      await harness.startPipe();
      await driver.click('#plain');

      const [first] = await handedOff();
      expect(first?.url).toBe(`${harness.testServer}/files/plain/${String(SIZE)}`);
      await waitFor(
        'the extension to be connected again',
        async () => (await driver.popupText()) === CONNECTED,
      );
    });

    it('[TC-BRW-004] blob: and data: downloads are not intercepted', async () => {
      await driver.click('#blob');
      await driver.click('#data');

      const expected = sha256Of('Kasi');
      await waitFor('both files', async () =>
        (await driver.files()).filter((f) => f.sha256 === expected).length === 2 ? true : false,
      );
      expect(await harness.received()).toEqual([]);
    });

    it('[TC-BRW-004] a POST-form download is not intercepted', async () => {
      await driver.click('#post');

      const expected = await fileSha('post', harness);
      await waitFor('the browser to finish the file', async () =>
        (await driver.files()).some((f) => f.sha256 === expected),
      );
      expect(await harness.received()).toEqual([]);
    });

    it('[TC-BRW-004] a download from an excluded site is not intercepted', async () => {
      await harness.setRules({ excludedSites: ['excluded.test'] });
      await driver.click('#excluded');

      const expected = await fileSha('excluded', harness);
      await waitFor('the browser to finish the file', async () =>
        (await driver.files()).some((f) => f.sha256 === expected),
      );
      expect(await harness.received()).toEqual([]);
    });

    // The native menu cannot be clicked by Playwright or WebDriver, so a page of the extension sends the click's data to the same
    // handler (docs/plans/m2-5.md, G5); the real right-click stays a manual row in docs/qa.
    const batchUrls = (from: number, to: number) =>
      Array.from(
        { length: to - from },
        (_, i) => `${harness.testServer}/files/doc${String(from + i)}/1000`,
      );
    const handedOffBatches = () =>
      waitFor('the link batch', async () => {
        const all = await harness.receivedBatches();
        return all.length > 0 ? all : false;
      });

    it('[TC-BRW-005] "Download all links" sends the 40 links of the page in one batch, with the referrer and the cookies', async () => {
      await driver.addCookie(CANARY_NAME, CANARY_VALUE, harness.testServer);
      await driver.open(`${harness.admin}/pages/links`);

      await driver.menuClick('kasi-download-all');

      const [batch, ...rest] = await handedOffBatches();
      expect(rest).toHaveLength(0);
      expect(batch?.urls).toEqual(batchUrls(0, 40));
      expect(batch?.referrer).toBe(`${harness.admin}/pages/links`);
      expect(batch?.cookies).toContainEqual(
        expect.objectContaining({ name: CANARY_NAME, value: CANARY_VALUE }),
      );
    });

    it('[TC-BRW-005] "Download selected links" sends only the 12 links that are selected', async () => {
      await driver.open(`${harness.admin}/pages/links`);
      await driver.selectText('#picked');

      await driver.menuClick('kasi-download-selected');

      const [batch, ...rest] = await handedOffBatches();
      expect(rest).toHaveLength(0);
      expect(batch?.urls).toEqual(batchUrls(0, 12));
    });

    it('[TC-BRW-004] "Download with Kasi" on a link sends that link to Kasi and the browser downloads nothing', async () => {
      await driver.addCookie(CANARY_NAME, CANARY_VALUE, harness.testServer);
      await driver.open(`${harness.admin}/pages/links`);
      const link = batchUrls(3, 4)[0] ?? '';

      await driver.menuClick('kasi-download-link', { linkUrl: link });

      const [first, ...rest] = await handedOff();
      expect(rest).toHaveLength(0);
      expect(first?.url).toBe(link);
      expect(first?.referrer).toBe(`${harness.admin}/pages/links`);
      expect(first?.cookies).toContainEqual(
        expect.objectContaining({ name: CANARY_NAME, value: CANARY_VALUE }),
      );
      await pause(1000);
      expect(await driver.folder()).toEqual([]);
    });

    it('[TC-BRW-004] refreshLink opens the page, takes its download and answers with the fresh URL and cookies; Kasi gets no new job', async () => {
      await driver.addCookie(CANARY_NAME, CANARY_VALUE, harness.testServer);
      await driver.open(home(harness));

      await harness.refreshLink(
        'job-refresh',
        `${harness.admin}/pages/refresh/fresh/${String(SIZE)}`,
      );

      const [answer, ...rest] = await waitFor('the refreshed link', async () => {
        const all = await harness.refreshed();
        return all.length > 0 ? all : false;
      });
      expect(rest).toHaveLength(0);
      expect(answer?.jobId).toBe('job-refresh');
      expect(answer?.url).toBe(`${harness.testServer}/files/fresh/${String(SIZE)}`);
      expect(answer?.cookies).toContainEqual(
        expect.objectContaining({ name: CANARY_NAME, value: CANARY_VALUE }),
      );
      await pause(1000);
      expect(await harness.received()).toEqual([]);
      expect(await driver.folder()).toEqual([]);
    });
  });

  suite(`end to end (${kit.family}): the host starts Kasi, then Kasi goes missing`, () => {
    let page: Harness;
    let host: HostSetup;
    let restore: () => void;
    let driver: BrowserDriver;
    let admin: string;
    let kasi: ReturnType<typeof attach>;
    let profile: string;

    const kasiIsDown = async () =>
      !(await kasi.received().then(
        () => true,
        () => false,
      ));

    beforeAll(async () => {
      const port = await freePort();
      admin = `http://127.0.0.1:${String(port)}`;
      kasi = attach(admin);
      profile = await mkdtemp(join(tmpdir(), 'kasi-profile-'));
      page = await startHarness({ stub: false });
      // the first Kasi starts at once, so the extension caches its rules (a fresh install intercepts nothing)
      host = await installHost({ adminPort: port, launchDelayMs: 0 });
      restore = registerHost(kit.family, await host.manifestPath(kit.family, kit.allowed));
      driver = await kit.launch(profile);
      await waitFor(
        'the extension to reach the started Kasi',
        async () => (await driver.popupText()) === CONNECTED,
        60_000,
      );
    });

    afterAll(async () => {
      await driver.close();
      restore();
      await stopTray(admin);
      await page.stop();
      await host.dispose();
      await rm(profile, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
    });

    it.skipIf(kit.rulesSurviveRestart === false)(
      '[TC-BRW-002] with Kasi not running the host starts it and the download is taken',
      async () => {
        // end the browser and Kasi; the next Kasi the host starts opens its pipe only after three seconds
        await driver.close();
        await stopTray(admin);
        await waitFor('Kasi to be gone', kasiIsDown);
        await host.setLaunchDelay(3000);

        driver = await kit.launch(profile); // the cached rules decide, and the extension has to wait for the host to start Kasi
        await driver.open(home(page));
        expect(await kasiIsDown()).toBe(true);
        await driver.click('#plain');

        const taken = await waitFor(
          'the hand-off through the started Kasi',
          async () => {
            const all = await kasi.received().catch(() => []);
            return all.length > 0 ? all : false;
          },
          60_000,
        );
        expect(taken[0]?.url).toBe(`${page.testServer}/files/plain/${String(SIZE)}`);
        await pause(1500);
        // a paused download still completes when all its bytes were in; what counts is that the extension removed the file
        expect(await driver.folder()).toEqual([]);
      },
    );

    it('[TC-BRW-002] after the user quits Kasi the popup stays not connected, and a download starts Kasi again through the host', async () => {
      await waitFor(
        'the extension to be connected',
        async () => (await driver.popupText()) === CONNECTED,
        60_000,
      );
      await stopTray(admin);
      await waitFor('Kasi to be gone', kasiIsDown);
      await waitFor(
        'the popup to say not connected',
        async () => (await driver.popupText()) === NOT_CONNECTED,
        15_000,
      );
      await pause(3000); // nothing relaunches Kasi on a timer, and opening the popup does not either
      expect(await kasiIsDown()).toBe(true);
      expect(await driver.popupText()).toBe(NOT_CONNECTED);
      if ((await driver.badge()) !== undefined) {
        await waitFor('the failure badge', async () => (await driver.badge()) === '!');
      }

      await host.setLaunchDelay(0);
      await driver.resetFiles();
      await driver.open(home(page));
      await driver.click('#plain');

      const taken = await waitFor(
        'the hand-off through the Kasi the host started',
        async () => {
          const all = await kasi.received().catch(() => []);
          return all.length > 0 ? all : false;
        },
        60_000,
      );
      expect(taken[0]?.url).toBe(`${page.testServer}/files/plain/${String(SIZE)}`);
    });

    it('[TC-BRW-002] with Kasi missing the browser keeps its own download and the popup shows disconnected', async () => {
      await waitFor(
        'the extension to be connected',
        async () => (await driver.popupText()) === CONNECTED,
      );
      // a running executable can be renamed but not deleted; the host then finds no Kasi.exe next to it (APP_NOT_INSTALLED)
      await rename(join(host.appDir, 'Kasi.exe'), join(host.appDir, 'Kasi.old'));
      await stopTray(admin);
      await waitFor(
        'the extension to notice',
        async () => (await driver.popupText()) === NOT_CONNECTED,
      );

      await driver.resetFiles();
      await driver.open(home(page));
      await driver.click('#attr');

      const expected = await page.sha256('attr', SIZE);
      await waitFor('the browser to finish the file', async () =>
        (await driver.files()).some((f) => f.sha256 === expected),
      );
      expect(await driver.popupText()).toBe(NOT_CONNECTED);
      if ((await driver.badge()) !== undefined) {
        await waitFor('the failure badge', async () => (await driver.badge()) === '!');
      }
    });
  });
}
