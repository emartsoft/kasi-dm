// Registers the native host for the browser under test and puts the registry back as it found it (plan 3.1, G12).
// The browsers only look in their own HKCU key, so the key cannot be test-private; instead the previous default value is saved
// and restored, and the manifest it points at lives in a temp folder that is deleted afterwards.
import { spawnSync } from 'node:child_process';

const HOST = 'com.martsoft.kasi';

export type Family = 'chromium' | 'firefox';

const keys: Record<Family, string> = {
  chromium: `HKCU\\Software\\Chromium\\NativeMessagingHosts\\${HOST}`,
  firefox: `HKCU\\Software\\Mozilla\\NativeMessagingHosts\\${HOST}`,
};

function reg(...args: string[]) {
  return spawnSync('reg.exe', args, { encoding: 'utf8', windowsHide: true });
}

function readDefault(key: string): { existed: boolean; value?: string } {
  const result = reg('query', key, '/ve');
  if (result.status !== 0) return { existed: false };
  const line = result.stdout.split(/\r?\n/).find((l) => l.includes('REG_SZ'));
  const value = line?.split('REG_SZ')[1]?.trim();
  return value ? { existed: true, value } : { existed: true };
}

/** Points the browser family's host registration at `manifest`; returns the function that restores what was there. */
export function registerHost(family: Family, manifest: string): () => void {
  const key = keys[family];
  const before = readDefault(key);
  const added = reg('add', key, '/ve', '/t', 'REG_SZ', '/d', manifest, '/f');
  if (added.status !== 0) throw new Error(`could not register the host: ${added.stderr}`);

  let restored = false;
  const restore = () => {
    if (restored) return;
    restored = true;
    if (before.existed && before.value) {
      reg('add', key, '/ve', '/t', 'REG_SZ', '/d', before.value, '/f');
    } else if (before.existed) {
      reg('delete', key, '/ve', '/f');
    } else {
      reg('delete', key, '/f');
    }
  };
  // a crashed or interrupted run must not leave the developer's registration replaced
  process.once('exit', restore);
  return restore;
}
