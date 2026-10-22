// What the scenarios need from a browser, whichever way it is driven (Playwright for Chromium, geckodriver for Firefox).
import { createHash } from 'node:crypto';
import { readdir, readFile, rm, stat } from 'node:fs/promises';
import { join } from 'node:path';

export type Modifier = 'Alt' | 'Control' | 'Shift';

export interface SavedFile {
  name: string;
  sha256: string;
}

export interface BrowserDriver {
  readonly family: 'chromium' | 'firefox';
  open(url: string): Promise<void>;
  click(selector: string, modifiers?: Modifier[]): Promise<void>;
  addCookie(name: string, value: string, siteUrl: string): Promise<void>;
  /** The downloads the browser finished, with their SHA-256. */
  files(): Promise<SavedFile[]>;
  /** Everything in the download folder, partial files included (a handed-off download must leave nothing). */
  folder(): Promise<string[]>;
  /** Deletes what is in the download folder, so one scenario's files do not leak into the next. */
  resetFiles(): Promise<void>;
  /** The text of the popup's status line. */
  popupText(): Promise<string>;
  /** The toolbar badge text, or undefined when this driver cannot read it. */
  badge(): Promise<string | undefined>;
  close(): Promise<void>;
}

export async function filesIn(dir: string): Promise<SavedFile[]> {
  const found: SavedFile[] = [];
  for (const name of await readdir(dir)) {
    const path = join(dir, name);
    if ((await stat(path)).isFile()) {
      found.push({
        name,
        sha256: createHash('sha256')
          .update(await readFile(path))
          .digest('hex'),
      });
    }
  }
  return found;
}

export async function emptyDirectory(dir: string): Promise<void> {
  for (const name of await readdir(dir))
    await rm(join(dir, name), { recursive: true, force: true });
}

export function sha256Of(text: string): string {
  return createHash('sha256').update(text).digest('hex');
}

/** Polls until `check` returns a value (not undefined/false) or the generous deadline passes. */
export async function waitFor<T>(
  what: string,
  check: () => Promise<T | undefined | false>,
  timeoutMs = 30_000,
): Promise<T> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const value = await check();
    if (value !== undefined && value !== false) return value;
    if (Date.now() > deadline) throw new Error(`timed out waiting for ${what}`);
    await new Promise((done) => setTimeout(done, 200));
  }
}

export function pause(ms: number): Promise<void> {
  return new Promise((done) => setTimeout(done, ms));
}
