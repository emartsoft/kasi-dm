import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { POST_GRACE_MS } from '../../src/shared/interception';
import { MARK_TTL_MS, createRequestTracker } from '../../src/shared/request-tracker';
import { ack, memoryArea, rig, rules, settle } from './rig';

vi.mock('../../src/shared/hash', () => import('./hash-mock'));

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

// Nothing is paused, no cookie is read and nothing is sent.
async function untouched(
  r: ReturnType<typeof rig>,
  over: Parameters<ReturnType<typeof rig>['add']>[0],
) {
  await r.start(r.add(over));
  expect(r.downloads.calls).toEqual([]);
  expect(r.cookieCalls).toEqual([]);
  expect(r.port().requests()).toHaveLength(0);
}

async function intercepted(
  r: ReturnType<typeof rig>,
  over: Parameters<ReturnType<typeof rig>['add']>[0] = {},
) {
  const done = r.start(r.add(over));
  await settle();
  const [request] = r.port().requests();
  expect(request).toBeDefined();
  r.port().receive(ack(request?.id ?? ''));
  await done;
  return request;
}

describe('[TC-BRW-004] downloads the extension never takes', () => {
  it.each([
    ['blob:', 'blob:https://files.example.org/5c1e'],
    ['data:', 'data:application/zip;base64,AAAA'],
    ['file:', 'file:///C:/pack.zip'],
    ['about:', 'about:blank'],
    ['an over-long URL', `https://files.example.org/${'a'.repeat(8200)}.zip`],
  ])('leaves a %s download to the browser', async (_name, url) => {
    const r = rig();
    r.online();
    await untouched(r, { url, finalUrl: url, filename: 'pack.zip' });
  });

  it('leaves a download whose final URL is blob: to the browser', async () => {
    const r = rig();
    r.online();
    await untouched(r, { finalUrl: 'blob:https://files.example.org/1' });
  });

  it('leaves another extension download alone, so there is no loop', async () => {
    const r = rig();
    r.online();
    await untouched(r, { byExtensionId: 'other@example' });
  });

  it('leaves an excluded site alone before any cookie is read', async () => {
    const r = rig();
    r.online();
    await untouched(r, {
      url: 'https://cdn.bank.example.com/s.zip',
      finalUrl: 'https://cdn.bank.example.com/s.zip',
    });
    await untouched(r, { referrer: 'https://bank.example.com/statements' });
  });

  it('leaves a POST-form download to the browser', async () => {
    const r = rig();
    r.online();
    await r.tracker.onRequest('https://files.example.org/pack.zip', 'POST');
    await untouched(r, {});
  });

  it('leaves a POST-form download to the browser when the browser reports the request after the download (Firefox)', async () => {
    let waited = 0;
    const late: { arrive?: () => Promise<void> } = {};
    const r = rig({
      wait: async (ms) => {
        waited = ms;
        await late.arrive?.();
      },
    });
    late.arrive = () => r.tracker.onRequest('https://files.example.org/pack.zip', 'POST');
    r.online();
    await untouched(r, {});
    expect(waited).toBe(POST_GRACE_MS);
  });

  it('keeps the mark through POST -> 302 -> GET, matching the final URL', async () => {
    const r = rig();
    r.online();
    await r.tracker.onRequest('https://files.example.org/form', 'POST');
    await r.tracker.onRedirect(
      'https://files.example.org/form',
      'https://cdn.example.org/pack.zip',
    );
    await untouched(r, {
      url: 'https://files.example.org/form',
      finalUrl: 'https://cdn.example.org/pack.zip',
    });
  });

  it('still intercepts after an ordinary GET', async () => {
    const r = rig();
    r.online();
    await r.tracker.onRequest('https://files.example.org/pack.zip', 'GET');
    await intercepted(r);
  });

  it('forgets a POST mark after 30 s', async () => {
    const r = rig();
    r.online();
    await r.tracker.onRequest('https://files.example.org/pack.zip', 'POST');
    await vi.advanceTimersByTimeAsync(MARK_TTL_MS + 1);
    await intercepted(r);
  });

  it('keeps marks in storage as hashes, for a restarted worker, at most 64', async () => {
    const area = memoryArea();
    const first = createRequestTracker(area);
    for (let i = 0; i < 70; i++) {
      await first.onRequest(`https://x.example.org/${String(i)}`, 'POST');
    }
    const second = createRequestTracker(area);
    expect(await second.isPost('https://x.example.org/69')).toBe(true);
    expect(await second.isPost('https://x.example.org/0')).toBe(false);
    expect(Object.keys(area.data['request-marks'] ?? {})).toHaveLength(64);
  });

  it('skips a private-window download unless the user allowed the extension there', async () => {
    const off = rig();
    off.online();
    await untouched(off, { incognito: true });

    const on = rig({ incognitoAllowed: true });
    on.online();
    await intercepted(on, { incognito: true });
    // The private window's own cookie store is used.
    expect(on.cookieCalls.length).toBeGreaterThan(0);
    expect(on.cookieCalls.every((c) => c.storeId === 'private-1')).toBe(true);
  });

  it('uses the default cookie store for an ordinary download', async () => {
    const r = rig();
    r.online();
    await intercepted(r);
    expect(r.cookieCalls.length).toBeGreaterThan(0);
    expect(r.cookieCalls.every((c) => c.storeId === undefined)).toBe(true);
  });

  it('is off when the host permission was revoked', async () => {
    const r = rig({ hostPermission: false });
    r.online();
    await untouched(r, {});
  });

  it('takes an ftp download, and sends no referrer that is not a web URL', async () => {
    const r = rig();
    r.online({ ...rules });
    const url = 'ftp://files.example.org/pack.zip';
    const request = await intercepted(r, {
      url,
      finalUrl: url,
      referrer: 'ftp://files.example.org/',
    });
    expect(request?.referrer).toBeUndefined();
  });
});
