import { readFileSync } from 'node:fs';
import { Ajv } from 'ajv';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { Cookie } from '../../src/types/protocol';
import { ack, CANARY_COOKIE, CANARY_QUERY, rig, rules, settle } from './rig';

vi.mock('../../src/shared/hash', () => import('./hash-mock'));

const schema = JSON.parse(
  readFileSync(
    new URL('../../../docs/protocol/kasi-messages.schema.json', import.meta.url),
    'utf8',
  ),
) as object;
const validate = new Ajv({ allErrors: true, strict: true }).compile(schema);

const secretCookie = (over: Partial<Cookie> = {}): Cookie => ({
  name: 'sid',
  value: CANARY_COOKIE,
  domain: '.example.org',
  path: '/',
  secure: true,
  httpOnly: true,
  ...over,
});

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

describe('[TC-BRW-001] a matching download goes to Kasi', () => {
  it('pauses, sends addDownload, and cancels then erases only after the ack', async () => {
    const r = rig({
      cookies: { 'https://files.example.org/pack.zip': [secretCookie()] },
    });
    r.online();
    const item = r.add();
    const done = r.start(item);
    await settle();

    const [request] = r.port().requests();
    expect(request).toMatchObject({
      type: 'addDownload',
      url: 'https://files.example.org/pack.zip',
      finalUrl: 'https://files.example.org/pack.zip',
      referrer: 'https://files.example.org/index.html',
      filename: 'pack.zip',
      mime: 'application/zip',
      userAgent: 'TestAgent/1.0',
    });
    expect(request?.cookies).toEqual([secretCookie()]);
    expect(validate(request)).toBe(true);
    // Nothing is cancelled while Kasi has not answered.
    expect(r.downloads.calls).toEqual(['pause:1']);

    r.port().receive(ack(request?.id ?? ''));
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'cancel:1', 'removeFile:1', 'erase:1']);
    expect(r.badge.text).toBe('');
  });

  it('matches on MIME type when the extension does not match', async () => {
    const r = rig();
    r.online();
    const get = 'https://files.example.org/get?id=1';
    const done = r.start(r.add({ filename: 'download', url: get, finalUrl: get }));
    await settle();
    expect(r.port().requests()).toHaveLength(1);
    r.port().receive(ack(r.port().requests()[0]?.id ?? ''));
    await done;
  });

  it('leaves a download that does not match alone, and reads no cookies for it', async () => {
    const r = rig();
    r.online();
    const item = r.add({
      filename: 'page.html',
      mime: 'text/html',
      url: 'https://x.example.org/p',
    });
    await r.start(item);
    expect(r.downloads.calls).toEqual([]);
    expect(r.cookieCalls).toEqual([]);
    expect(r.port().requests()).toHaveLength(0);
  });

  it('intercepts nothing before any rules exist', async () => {
    const r = rig();
    const item = r.add();
    await r.start(item);
    expect(r.downloads.calls).toEqual([]);
  });

  it('intercepts nothing when Kasi turned interception off, or the list is empty', async () => {
    const off = rig();
    off.online({ ...rules, enabled: false });
    await off.start(off.add());
    expect(off.downloads.calls).toEqual([]);

    const empty = rig();
    empty.online({ ...rules, extensions: [], mimeTypes: [] });
    await empty.start(empty.add());
    expect(empty.downloads.calls).toEqual([]);
  });

  it('decides from the cached rules while the worker is cold, then waits for Kasi', async () => {
    const r = rig({ cachedRules: rules });
    r.native.start(); // the worker was woken by this very download
    const done = r.start(r.add());
    await settle();
    expect(r.downloads.calls).toEqual(['pause:1']);
    expect(r.port().requests()).toHaveLength(0);

    r.port().receive(rules);
    await settle();
    const request = r.port().requests()[0];
    expect(request).toBeDefined();
    r.port().receive(ack(request?.id ?? ''));
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'cancel:1', 'removeFile:1', 'erase:1']);
  });

  it('reads cookies for the URL and the final URL, de-duplicated, with the domain unchanged', async () => {
    const hostOnly = secretCookie({ name: 'a', domain: 'files.example.org' });
    const domain = secretCookie({ name: 'b', domain: '.example.org' });
    const r = rig({
      cookies: {
        'https://dl.example.org/start.zip': [hostOnly, domain],
        'https://cdn.example.net/pack.zip': [
          domain,
          secretCookie({ name: 'c', domain: 'cdn.example.net' }),
        ],
      },
    });
    r.online();
    const done = r.start(
      r.add({
        url: 'https://dl.example.org/start.zip',
        finalUrl: 'https://cdn.example.net/pack.zip',
      }),
    );
    await settle();
    const request = r.port().requests()[0];
    expect(request?.cookies.map((c) => `${c.name}@${c.domain}`).sort()).toEqual([
      'a@files.example.org',
      'b@.example.org',
      'c@cdn.example.net',
    ]);
    r.port().receive(ack(request?.id ?? ''));
    await done;
  });

  it('drops a cookie value over 4096 characters and caps the list at 200', async () => {
    const many = Array.from({ length: 230 }, (_, i) => secretCookie({ name: `c${String(i)}` }));
    const r = rig({
      cookies: {
        'https://files.example.org/pack.zip': [
          ...many,
          secretCookie({ name: 'huge', value: 'x'.repeat(4097) }),
        ],
      },
    });
    r.online();
    const done = r.start(r.add());
    await settle();
    const request = r.port().requests()[0];
    expect(request?.cookies).toHaveLength(200);
    expect(request?.cookies.some((c) => c.name === 'huge')).toBe(false);
    expect(validate(request)).toBe(true);
    r.port().receive(ack(request?.id ?? ''));
    await done;
  });

  it('removes the file between cancel and erase in both browsers (a part file, or bytes that completed while paused)', async () => {
    for (const firefox of [true, false]) {
      const r = rig({ firefox });
      r.online();
      const done = r.start(r.add());
      await settle();
      r.port().receive(ack(r.port().requests()[0]?.id ?? ''));
      await done;
      expect(r.downloads.calls).toEqual(['pause:1', 'cancel:1', 'removeFile:1', 'erase:1']);
    }
  });

  it('runs concurrent downloads independently, each with its own request id', async () => {
    const r = rig();
    r.online();
    const runs = [1, 2, 3].map((id) =>
      r.start(
        r.add({
          id,
          url: `https://f.example.org/${String(id)}.zip`,
          finalUrl: `https://f.example.org/${String(id)}.zip`,
        }),
      ),
    );
    await settle();
    const requests = r.port().requests();
    expect(new Set(requests.map((q) => q.id)).size).toBe(3);
    // Answer in reverse order; the second is left to time out.
    r.port().receive(ack(requests[2]?.id ?? ''));
    r.port().receive(ack(requests[0]?.id ?? ''));
    await vi.advanceTimersByTimeAsync(2000);
    await Promise.all(runs);
    expect(r.downloads.items.has(1)).toBe(false);
    expect(r.downloads.items.has(3)).toBe(false);
    expect(r.downloads.items.get(2)?.paused).toBe(false);
  });
});

describe('[TC-BRW-002] Kasi is missing, slow or refuses: the browser keeps the download', () => {
  it('resumes and shows the disconnected badge when Kasi answers with an error', async () => {
    const r = rig();
    r.online();
    const done = r.start(r.add());
    await settle();
    const id = r.port().requests()[0]?.id ?? '';
    r.port().receive({ v: 1, id, type: 'error', code: 'INTERNAL' });
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
    expect(r.badge.text).toBe('!');
  });

  it('resumes after the 2 s ack window (the rules value), not before', async () => {
    const r = rig();
    r.online({ ...rules, ackTimeoutMs: 2000 });
    const done = r.start(r.add());
    await settle();
    await vi.advanceTimersByTimeAsync(1999);
    expect(r.downloads.calls).toEqual(['pause:1']);
    await vi.advanceTimersByTimeAsync(1);
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
    expect(r.badge.text).toBe('!');
  });

  it('resumes when the connection drops while waiting for the ack', async () => {
    const r = rig();
    r.online();
    const done = r.start(r.add());
    await settle();
    r.port().drop();
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
  });

  it('waits up to 6 s for Kasi to start, then hands the download over', async () => {
    const r = rig({ cachedRules: rules });
    r.native.start(); // hello is in flight: the host is launching Kasi
    const done = r.start(r.add());
    await settle();
    await vi.advanceTimersByTimeAsync(5000);
    expect(r.downloads.calls).toEqual(['pause:1']);
    r.port().receive(rules);
    await settle();
    const request = r.port().requests()[0];
    expect(request).toBeDefined();
    r.port().receive(ack(request?.id ?? ''));
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'cancel:1', 'removeFile:1', 'erase:1']);
  });

  it('gives up after the 6 s ready wait: resume, badge, and no request ever sent', async () => {
    const r = rig({ cachedRules: rules });
    r.native.start();
    const done = r.start(r.add());
    await settle();
    await vi.advanceTimersByTimeAsync(5999);
    expect(r.downloads.calls).toEqual(['pause:1']);
    await vi.advanceTimersByTimeAsync(1);
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
    expect(r.badge.text).toBe('!');
    expect(r.port().requests()).toHaveLength(0);
  });

  it('does not treat APP_NOT_RUNNING as connected: the request is never sent', async () => {
    const r = rig({ cachedRules: rules });
    r.native.start();
    const done = r.start(r.add());
    await settle();
    r.port().receive({ v: 1, id: 'id1', type: 'error', code: 'APP_NOT_RUNNING' });
    await settle();
    expect(r.native.isConnected()).toBe(false);
    await vi.advanceTimersByTimeAsync(6000);
    await done;
    expect(r.port().requests()).toHaveLength(0);
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
  });

  it('waits for nothing when Kasi is not installed', async () => {
    const r = rig({ cachedRules: rules });
    r.native.start();
    r.port().receive({ v: 1, id: 'id1', type: 'error', code: 'APP_NOT_INSTALLED' });
    const done = r.start(r.add());
    await done; // no timer advance: nothing was waited for
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
    expect(r.badge.text).toBe('!');
  });

  it('waits for nothing when no native host is registered', async () => {
    const r = rig({ cachedRules: rules });
    r.native.start();
    r.port().drop(); // connectNative failed at once
    const done = r.start(r.add());
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
  });

  it('does not resume a download the user cancelled meanwhile', async () => {
    const r = rig();
    r.online();
    const item = r.add();
    const done = r.start(item);
    await settle();
    item.state = 'interrupted';
    await vi.advanceTimersByTimeAsync(2000);
    await done;
    expect(r.downloads.calls).toEqual(['pause:1']);
  });

  it('leaves a download alone when it cannot be paused or already finished', async () => {
    const failing = rig();
    failing.online();
    failing.downloads.failPause = true;
    await failing.start(failing.add());
    expect(failing.downloads.calls).toEqual(['pause:1']);
    expect(failing.cookieCalls).toEqual([]);

    const finished = rig();
    finished.online();
    await finished.start(finished.add({ state: 'complete' }));
    expect(finished.downloads.calls).toEqual([]);
  });

  it('cancels the browser copy when the ack comes late and the download is still running', async () => {
    const r = rig();
    r.online();
    const done = r.start(r.add());
    await settle();
    const id = r.port().requests()[0]?.id ?? '';
    await vi.advanceTimersByTimeAsync(2000);
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);

    await vi.advanceTimersByTimeAsync(5000);
    r.port().receive(ack(id));
    await settle();
    expect(r.downloads.calls).toEqual([
      'pause:1',
      'resume:1',
      'cancel:1',
      'removeFile:1',
      'erase:1',
    ]);
  });

  it('leaves a late-acked download alone when its bytes are already complete', async () => {
    const r = rig();
    r.online();
    const item = r.add();
    const done = r.start(item);
    await settle();
    const id = r.port().requests()[0]?.id ?? '';
    await vi.advanceTimersByTimeAsync(2000);
    await done;
    item.state = 'complete';
    r.port().receive(ack(id));
    await settle();
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
  });

  it('ignores a late ack after 30 s, and acks it never sent a request for', async () => {
    const r = rig();
    r.online();
    const done = r.start(r.add());
    await settle();
    const id = r.port().requests()[0]?.id ?? '';
    await vi.advanceTimersByTimeAsync(2000);
    await done;
    await vi.advanceTimersByTimeAsync(30_001);
    r.port().receive(ack(id));
    r.port().receive(ack('never-sent'));
    await settle();
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
  });
});

describe('[TC-BRW-001] [TC-BRW-002] nothing sensitive is logged or shown', () => {
  const url = `https://files.example.org/pack.zip?token=${CANARY_QUERY}`;
  const cookies = { [url]: [secretCookie()] };

  async function run(finish: (r: ReturnType<typeof rig>, id: string) => void | Promise<void>) {
    const r = rig({ cookies });
    r.online();
    const done = r.start(r.add({ url, finalUrl: url, referrer: url }));
    await settle();
    await finish(r, r.port().requests()[0]?.id ?? '');
    await vi.advanceTimersByTimeAsync(2000);
    await done;
    return r;
  }

  it.each([
    [
      'success',
      (r: ReturnType<typeof rig>, id: string) => {
        r.port().receive(ack(id));
      },
    ],
    [
      'an error reply',
      (r: ReturnType<typeof rig>, id: string) => {
        r.port().receive({ v: 1, id, type: 'error', code: 'INTERNAL', detail: 'x' });
      },
    ],
    ['a timeout', () => undefined],
    [
      'a disconnect',
      (r: ReturnType<typeof rig>) => {
        r.port().drop();
      },
    ],
  ])('keeps cookie values and query strings out of the logs after %s', async (_name, finish) => {
    const r = await run(finish);
    expect(r.logs.length).toBeGreaterThan(0);
    expect(r.allLogs()).not.toContain(CANARY_COOKIE);
    expect(r.allLogs()).not.toContain(CANARY_QUERY);
    expect(r.badge.text).not.toContain('CANARY');
  });

  it('keeps them out of an error, and does not resume a download Kasi already accepted', async () => {
    const r = rig({ cookies });
    r.online();
    r.downloads.cancel = () => Promise.reject(new Error(`boom ${url}`));
    const done = r.start(r.add({ url, finalUrl: url }));
    await settle();
    r.port().receive(ack(r.port().requests()[0]?.id ?? ''));
    await done;
    expect(r.logs.some((entry) => JSON.stringify(entry).includes('boom'))).toBe(true);
    expect(r.allLogs()).not.toContain(CANARY_QUERY);
    expect(r.allLogs()).not.toContain(CANARY_COOKIE);
    expect(r.downloads.calls).not.toContain('resume:1');
  });
});
