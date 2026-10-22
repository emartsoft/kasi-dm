// Starts tests/Kasi.BrowserHarness (the stub Kasi, the test pages and Kasi.TestServer) and the registered native host for one run.
// Nothing here logs a URL or a cookie value; the harness itself touches loopback only.
import { spawn, type ChildProcess } from 'node:child_process';
import { copyFile, cp, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { createServer } from 'node:net';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const repo = fileURLToPath(new URL('../../../../', import.meta.url));

export interface HarnessRules {
  enabled?: boolean;
  extensions?: string[];
  mimeTypes?: string[];
  excludedSites?: string[];
  bypassKey?: 'alt' | 'ctrl' | 'shift' | 'none';
  ackTimeoutMs?: number;
}

export interface HarnessConfig {
  stub?: boolean;
  adminPort?: number;
  launchDelayMs?: number;
  mode?: 'ack' | 'error' | 'silent';
  answerDelayMs?: number;
  rules?: HarnessRules;
}

export interface Received {
  url: string;
  finalUrl: string;
  referrer: string | null;
  filename: string;
  mime: string;
  cookies: { name: string; value: string; domain: string }[];
}

export interface Harness {
  admin: string;
  testServer: string;
  received(): Promise<Received[]>;
  clear(): Promise<void>;
  setMode(mode: 'ack' | 'error' | 'silent', answerDelayMs?: number): Promise<void>;
  setRules(rules: HarnessRules): Promise<void>;
  sha256(seed: string, size: number): Promise<string>;
  stop(): Promise<void>;
}

export function harnessDirectory(): string {
  return (
    process.env.KASI_HARNESS_DIR ?? join(repo, 'tests/Kasi.BrowserHarness/bin/Release/net10.0')
  );
}

export function nativeHostPath(): string {
  return (
    process.env.KASI_NATIVEHOST_AOT ??
    join(repo, 'src/Kasi.NativeHost/bin/Release/net10.0/win-x64/publish/Kasi.NativeHost.exe')
  );
}

/** Why the end-to-end tests cannot run here, or undefined when they can. Run on CI; opt in locally with KASI_E2E=1. */
export function unavailableReason(): string | undefined {
  if (process.platform !== 'win32') return 'the native host and the pipe are Windows only';
  if (process.env.KASI_E2E !== '1') {
    return 'KASI_E2E=1 is not set (the run registers a native messaging host under HKCU and restores it afterwards)';
  }
  if (!existsSync(nativeHostPath())) {
    return `the native host is not published (dotnet publish src/Kasi.NativeHost -c Release -r win-x64; or set KASI_NATIVEHOST_AOT)`;
  }
  if (!existsSync(join(harnessDirectory(), 'Kasi.BrowserHarness.exe'))) {
    return 'tests/Kasi.BrowserHarness is not built (dotnet build tests/Kasi.BrowserHarness -c Release; or set KASI_HARNESS_DIR)';
  }
  return undefined;
}

export async function freePort(): Promise<number> {
  return new Promise((done, fail) => {
    const server = createServer();
    server.once('error', fail);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => {
        if (address && typeof address === 'object') done(address.port);
        else fail(new Error('no port'));
      });
    });
  });
}

async function json<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  if (!response.ok) throw new Error(`harness answered ${String(response.status)}`);
  return (await response.json()) as T;
}

function send(url: string, method: string, body?: unknown): Promise<Response> {
  return fetch(url, {
    method,
    ...(body === undefined
      ? {}
      : { headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) }),
  });
}

function api(admin: string): Omit<Harness, 'testServer' | 'stop'> {
  return {
    admin,
    received: () => json<Received[]>(`${admin}/_stub/received`),
    clear: async () => {
      await send(`${admin}/_stub/received`, 'DELETE');
    },
    setMode: async (mode, answerDelayMs = 0) => {
      await send(`${admin}/_stub/mode`, 'POST', { mode, answerDelayMs });
    },
    setRules: async (rules) => {
      await send(`${admin}/_stub/rules`, 'POST', rules);
    },
    sha256: async (seed, size) =>
      (await (await fetch(`${admin}/_stub/sha256/${seed}/${String(size)}`)).text()).toLowerCase(),
  };
}

/** Adopts a stub that something else started (the tray process the native host launched). */
export function attach(admin: string): Omit<Harness, 'testServer' | 'stop'> {
  return api(admin);
}

export async function startHarness(config: HarnessConfig = {}): Promise<Harness> {
  const dir = await mkdtemp(join(tmpdir(), 'kasi-harness-'));
  const file = join(dir, 'config.json');
  await writeFile(file, JSON.stringify(config));
  const child: ChildProcess = spawn(
    join(harnessDirectory(), 'Kasi.BrowserHarness.exe'),
    ['serve', '--config', file],
    { stdio: ['pipe', 'pipe', 'inherit'], windowsHide: true },
  );
  const ready = await new Promise<{ admin: string; testServer: string }>((done, fail) => {
    let text = '';
    const timer = setTimeout(() => {
      fail(new Error('the harness did not start within 30 s'));
    }, 30_000);
    child.stdout?.on('data', (chunk: Buffer) => {
      text += chunk.toString();
      const line = text.split('\n')[0];
      if (text.includes('\n') && line) {
        clearTimeout(timer);
        done(JSON.parse(line) as { admin: string; testServer: string });
      }
    });
    child.once('exit', (code) => {
      clearTimeout(timer);
      fail(
        new Error(
          `the harness ended with code ${String(code)} (a real Kasi or another harness may own the pipe)`,
        ),
      );
    });
  });
  return {
    ...api(ready.admin),
    testServer: ready.testServer,
    stop: async () => {
      child.stdin?.end();
      await new Promise<void>((done) => {
        if (child.exitCode !== null) done();
        child.once('exit', () => {
          done();
        });
        setTimeout(() => {
          child.kill();
          done();
        }, 10_000).unref();
      });
      await rm(dir, { recursive: true, force: true });
    },
  };
}

export interface HostSetup {
  /** The folder holding Kasi.NativeHost.exe (and Kasi.exe when kasi is 'tray'). */
  appDir: string;
  /** The host manifest to register for a browser. */
  manifestPath(browser: 'chromium' | 'firefox', allowed: string): Promise<string>;
  /** Rewrites the tray config: how long the next Kasi.exe the host starts waits before it opens the pipe. */
  setLaunchDelay(launchDelayMs: number): Promise<void>;
  /** Removes the folder (the tray process is stopped first by the caller). */
  dispose(): Promise<void>;
}

/**
 * Copies the real native host into a temp folder. With `tray` the harness is copied beside it as Kasi.exe, so the host's launcher
 * (TC-BRW-002) finds a Kasi to start; without it there is no Kasi.exe and the host answers APP_NOT_INSTALLED.
 */
export async function installHost(tray?: {
  adminPort: number;
  launchDelayMs: number;
  rules?: HarnessRules;
}): Promise<HostSetup> {
  const root = await mkdtemp(join(tmpdir(), 'kasi-e2e-'));
  const appDir = join(root, 'app');
  await mkdir(appDir);
  await copyFile(nativeHostPath(), join(appDir, 'Kasi.NativeHost.exe'));
  if (tray) {
    await cp(harnessDirectory(), appDir, { recursive: true });
    await copyFile(join(appDir, 'Kasi.BrowserHarness.exe'), join(appDir, 'Kasi.exe'));
  }
  const writeTray = (launchDelayMs: number) =>
    writeFile(
      join(appDir, 'kasi-harness.json'),
      JSON.stringify({ adminPort: tray?.adminPort, launchDelayMs, rules: tray?.rules }),
    );
  if (tray) await writeTray(tray.launchDelayMs);
  const template = await readFile(
    resolve(repo, 'src/Kasi.NativeHost/Registration/com.martsoft.kasi.json'),
    'utf8',
  );
  return {
    appDir,
    manifestPath: async (browser, allowed) => {
      const key = browser === 'chromium' ? 'allowed_origins' : 'allowed_extensions';
      const value = browser === 'chromium' ? `chrome-extension://${allowed}/` : allowed;
      const manifest = template
        .replace('@HOST_PATH@', join(appDir, 'Kasi.NativeHost.exe').replaceAll('\\', '\\\\'))
        .replace('@ALLOW_KEY@', key)
        .replace('@ALLOWED@', JSON.stringify(value));
      const path = join(root, `com.martsoft.kasi.${browser}.json`);
      await writeFile(path, manifest);
      return path;
    },
    setLaunchDelay: writeTray,
    dispose: async () => {
      // a Kasi.exe that was just asked to quit may still hold its file for a moment
      await rm(root, { recursive: true, force: true, maxRetries: 20, retryDelay: 250 });
    },
  };
}

/** Asks the tray Kasi the native host started (it is not our child) to exit; it is never killed by image name. */
export async function stopTray(admin: string): Promise<void> {
  try {
    await send(`${admin}/_stub/quit`, 'POST');
  } catch {
    // already gone
  }
}
