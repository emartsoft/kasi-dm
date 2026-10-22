// A small WebDriver (classic) client over fetch, enough to drive Firefox through geckodriver (plan 3.2: Playwright cannot load Firefox
// extensions). geckodriver is a development tool found on the CI runner or named by GECKODRIVER; it is never shipped (MPL-2.0).
import { spawn, spawnSync, type ChildProcess } from 'node:child_process';
import { existsSync } from 'node:fs';
import { freePort } from './harness';
import { pause } from './driver';

const ELEMENT = 'element-6066-11e4-a52e-4f735466cecf';

export function findGeckodriver(): string | undefined {
  const named = process.env.GECKODRIVER;
  if (named) return existsSync(named) ? named : undefined;
  const found = spawnSync('where.exe', ['geckodriver'], { encoding: 'utf8', windowsHide: true });
  return found.status === 0 ? found.stdout.split(/\r?\n/)[0]?.trim() : undefined;
}

interface WireValue<T> {
  value: T;
}

export class WebDriverSession {
  private constructor(
    private readonly base: string,
    private readonly sessionId: string,
    private readonly process: ChildProcess,
  ) {}

  /** Starts geckodriver and a Firefox session with the given capabilities (`moz:firefoxOptions`). */
  static async start(
    geckodriver: string,
    firefoxOptions: Record<string, unknown>,
  ): Promise<WebDriverSession> {
    const port = await freePort();
    const child = spawn(geckodriver, ['--port', String(port), '--host', '127.0.0.1'], {
      stdio: 'ignore',
      windowsHide: true,
    });
    const base = `http://127.0.0.1:${String(port)}`;
    try {
      for (let attempt = 0; ; attempt++) {
        try {
          if ((await fetch(`${base}/status`)).ok) break;
        } catch {
          // not listening yet
        }
        if (attempt > 100) throw new Error('geckodriver did not start');
        await pause(100);
      }
      const response = await fetch(`${base}/session`, {
        method: 'POST',
        body: JSON.stringify({
          capabilities: {
            alwaysMatch: { browserName: 'firefox', 'moz:firefoxOptions': firefoxOptions },
          },
        }),
      });
      const body = (await response.json()) as WireValue<{ sessionId?: string; message?: string }>;
      if (!response.ok || !body.value.sessionId) {
        throw new Error(
          `could not start Firefox: ${body.value.message ?? String(response.status)}`,
        );
      }
      return new WebDriverSession(base, body.value.sessionId, child);
    } catch (error) {
      child.kill();
      throw error;
    }
  }

  private async call<T>(method: string, path: string, body?: unknown): Promise<T> {
    const response = await fetch(`${this.base}/session/${this.sessionId}${path}`, {
      method,
      ...(body === undefined ? {} : { body: JSON.stringify(body) }),
    });
    const parsed = (await response.json()) as WireValue<T & { error?: string; message?: string }>;
    if (!response.ok) {
      // geckodriver's messages can carry page URLs; keep only the error code
      throw new Error(
        `WebDriver ${method} ${path} failed: ${parsed.value.error ?? String(response.status)}`,
      );
    }
    return parsed.value;
  }

  async installAddon(path: string): Promise<void> {
    await this.call('POST', '/moz/addon/install', { path, temporary: true });
  }

  async navigate(url: string): Promise<void> {
    await this.call('POST', '/url', { url });
  }

  async find(css: string): Promise<string> {
    const element = await this.call<Record<string, string>>('POST', '/element', {
      using: 'css selector',
      value: css,
    });
    const id = element[ELEMENT];
    if (!id) throw new Error('element not found');
    return id;
  }

  /** A pointer click on an element with the given keys held (W3C actions), as a person Alt-clicking would. */
  async click(css: string, keys: string[] = []): Promise<void> {
    const element = await this.find(css);
    const codes: Record<string, string> = { Alt: '', Control: '', Shift: '' };
    const held = keys.map((k) => codes[k] ?? '');
    await this.call('POST', '/actions', {
      actions: [
        {
          type: 'key',
          id: 'keyboard',
          actions: [
            ...held.map((value) => ({ type: 'keyDown', value })),
            { type: 'pause', duration: 400 },
            ...held.map((value) => ({ type: 'keyUp', value })),
          ],
        },
        {
          type: 'pointer',
          id: 'mouse',
          parameters: { pointerType: 'mouse' },
          actions: [
            { type: 'pointerMove', origin: { [ELEMENT]: element }, x: 0, y: 0 },
            { type: 'pointerDown', button: 0 },
            { type: 'pointerUp', button: 0 },
          ],
        },
      ],
    });
    await this.call('DELETE', '/actions');
  }

  async addCookie(name: string, value: string): Promise<void> {
    await this.call('POST', '/cookie', { cookie: { name, value } });
  }

  async text(css: string): Promise<string> {
    return this.call<string>('GET', `/element/${await this.find(css)}/text`);
  }

  /** Runs `body` in a fresh tab and returns to the first one. */
  async inNewTab<T>(body: () => Promise<T>): Promise<T> {
    const [first] = await this.call<string[]>('GET', '/window/handles');
    const opened = await this.call<{ handle: string }>('POST', '/window/new', { type: 'tab' });
    await this.call('POST', '/window', { handle: opened.handle });
    try {
      return await body();
    } finally {
      await this.call('DELETE', '/window');
      if (first) await this.call('POST', '/window', { handle: first });
    }
  }

  async quit(): Promise<void> {
    try {
      await this.call('DELETE', '');
    } finally {
      this.process.kill();
    }
  }
}
