// KASI-BRW-004 / TRD 6.6, plan m2-5 2.5: refreshLink. The watcher runs behind the real claims seam of the interception and the
// real port manager; the tab and download APIs are fakes.
import { readFileSync } from 'node:fs';
import { Ajv } from 'ajv';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createLogger } from '../../src/shared/logger';
import { parseRefreshLink } from '../../src/shared/messages';
import { createRefreshLink, REFRESH_TIMEOUT_MS } from '../../src/shared/refresh-link';
import type { Cookie, RefreshLink } from '../../src/types/protocol';
import { ack, CANARY_COOKIE, CANARY_QUERY, rig, settle } from './rig';

vi.mock('../../src/shared/hash', () => import('./hash-mock'));

const schema = JSON.parse(
  readFileSync(
    new URL('../../../docs/protocol/kasi-messages.schema.json', import.meta.url),
    'utf8',
  ),
) as object;
const validate = new Ajv({ allErrors: true, strict: true }).compile(schema);

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

const cookie: Cookie = {
  name: 'sid',
  value: CANARY_COOKIE,
  domain: '.example.org',
  path: '/',
  secure: true,
  httpOnly: true,
};
const PAGE = 'https://files.example.org/get/pack';
const FILE = 'https://files.example.org/pack.zip';
const message = (over: Partial<RefreshLink> = {}): RefreshLink => ({
  v: 1,
  id: 'k1',
  type: 'refreshLink',
  jobId: 'job-1',
  referrerUrl: PAGE,
  ...over,
});

function setup(options: { failTabs?: boolean } = {}) {
  const tabs = { opened: [] as string[], closed: [] as number[] };
  const holder: { current?: ReturnType<typeof createRefreshLink> } = {};
  const r = rig({ claims: [(item) => holder.current?.claim(item) ?? false] });
  const refresh = createRefreshLink({
    tabs: {
      create: (url) => {
        if (options.failTabs) return Promise.reject(new Error('no window'));
        tabs.opened.push(url);
        return Promise.resolve({ id: 40 + tabs.opened.length });
      },
      remove: (tabId) => {
        tabs.closed.push(tabId);
        return Promise.resolve();
      },
    },
    downloads: {
      cancel: (id) => r.downloads.cancel(id),
      erase: (query) => r.downloads.erase(query),
      removeFile: (id) => r.downloads.removeFile(id),
    },
    cookiesFor: () => Promise.resolve([cookie]),
    port: r.native,
    newId: () => 'lr1',
    logger: createLogger({
      debug: (...a: unknown[]) => r.logs.push(a),
      info: (...a: unknown[]) => r.logs.push(a),
      warn: (...a: unknown[]) => r.logs.push(a),
      error: (...a: unknown[]) => r.logs.push(a),
    }),
  });
  holder.current = refresh;
  r.online();
  const sent = () => r.port().sent.filter((m) => m.type === 'linkRefreshed');
  return { r, tabs, refresh, sent };
}

describe('[TC-BRW-004] refreshLink: parsing', () => {
  it('accepts what the schema accepts and nothing else', () => {
    for (const m of [message(), message({ matchHint: 'pack.zip' })]) {
      expect(validate(m)).toBe(true);
      expect(parseRefreshLink(m)).toEqual(m);
    }
    const bad = [
      { ...message(), extra: 1 },
      message({ referrerUrl: 'ftp://files.example.org/x' }),
      message({ referrerUrl: 'javascript:alert(1)' }),
      message({ referrerUrl: 'https:///nohost' }),
      message({ matchHint: '' }),
      message({ jobId: '' }),
    ];
    for (const m of bad) {
      expect(validate(m)).toBe(false);
      expect(parseRefreshLink(m)).toBeUndefined();
    }
  });
});

describe('[TC-BRW-004] refreshLink: the watch', () => {
  it('opens the page in a background tab and, on the first download, sends the fresh URL and cookies and closes the tab', async () => {
    const t = setup();
    await t.refresh.start(message());
    expect(t.tabs.opened).toEqual([PAGE]);

    const url = `${FILE}?token=${CANARY_QUERY}`;
    const done = t.r.start(t.r.add({ id: 7, url, finalUrl: url }));
    await settle();
    t.r.port().receive(ack('lr1'));
    await done;

    expect(t.r.downloads.calls).toEqual(['cancel:7', 'removeFile:7', 'erase:7']);
    const [sent] = t.sent();
    expect(validate(sent)).toBe(true);
    expect(sent).toMatchObject({
      jobId: 'job-1',
      url,
      cookies: [{ name: 'sid', value: CANARY_COOKIE }],
    });
    expect(t.tabs.closed).toEqual([41]);
  });

  it('the claimed download is never also handed to Kasi as a new job', async () => {
    const t = setup();
    await t.refresh.start(message());
    const done = t.r.start(t.r.add({ id: 7, url: FILE, finalUrl: FILE }));
    await settle();
    t.r.port().receive(ack('lr1'));
    await done;
    expect(t.r.port().requests()).toHaveLength(0);
    expect(t.r.downloads.calls).not.toContain('pause:7');
  });

  it('without a watch, a download is intercepted as usual and nothing is claimed', async () => {
    const t = setup();
    const done = t.r.start(t.r.add({ id: 7 }));
    await settle();
    expect(t.r.downloads.calls).toContain('pause:7');
    t.r.port().receive(ack(String(t.r.port().requests()[0]?.id)));
    await done;
    expect(t.sent()).toHaveLength(0);
  });

  it('with a hint, only a download whose file name or URL carries it is taken', async () => {
    const t = setup();
    await t.refresh.start(message({ matchHint: 'PACK.zip' }));

    const other = 'https://x.example.org/other.iso';
    expect(
      await t.refresh.claim(t.r.add({ id: 1, filename: '', url: other, finalUrl: other })),
    ).toBe(false);
    const claim = t.refresh.claim(
      t.r.add({
        id: 2,
        filename: 'C:\\Users\\me\\Downloads\\pack.zip',
        url: other,
        finalUrl: other,
      }),
    );
    await settle();
    t.r.port().receive(ack('lr1'));
    expect(await claim).toBe(true);
    expect(t.sent()).toHaveLength(1);
  });

  it('takes only the first matching download: the watch ends with it', async () => {
    const t = setup();
    await t.refresh.start(message());
    const first = t.refresh.claim(t.r.add({ id: 1 }));
    await settle();
    t.r.port().receive(ack('lr1'));
    expect(await first).toBe(true);
    expect(await t.refresh.claim(t.r.add({ id: 2 }))).toBe(false);
  });

  it('gives up after 60 s: nothing is sent, the tab is closed, a later download is left alone', async () => {
    const t = setup();
    await t.refresh.start(message());

    await vi.advanceTimersByTimeAsync(REFRESH_TIMEOUT_MS);

    expect(t.tabs.closed).toEqual([41]);
    expect(await t.refresh.claim(t.r.add({ id: 1 }))).toBe(false);
    expect(t.sent()).toHaveLength(0);
  });

  it('a second request for the same job replaces the first and closes its tab', async () => {
    const t = setup();
    await t.refresh.start(message());
    await t.refresh.start(message({ id: 'k2' }));
    expect(t.tabs.closed).toEqual([41]);
    expect(t.tabs.opened).toHaveLength(2);
  });

  it('a page that cannot be opened ends the watch without sending anything', async () => {
    const t = setup({ failTabs: true });
    await t.refresh.start(message());
    expect(await t.refresh.claim(t.r.add({ id: 1 }))).toBe(false);
    expect(t.sent()).toHaveLength(0);
  });

  it('a refreshed URL Kasi cannot take (a blob) is dropped from the browser and not sent', async () => {
    const t = setup();
    await t.refresh.start(message());
    const blob = 'blob:https://files.example.org/abc';
    const claim = t.refresh.claim(t.r.add({ id: 1, url: blob, finalUrl: blob }));
    await settle();
    expect(await claim).toBe(true);
    expect(t.sent()).toHaveLength(0);
    expect(t.r.downloads.calls).toContain('erase:1');
  });

  it('a refusal from Kasi is not fatal: the download is still gone and the tab closed', async () => {
    const t = setup();
    await t.refresh.start(message());
    const claim = t.refresh.claim(t.r.add({ id: 1 }));
    await settle();
    t.r.port().receive({ v: 1, id: 'lr1', type: 'error', code: 'INTERNAL' });
    expect(await claim).toBe(true);
    expect(t.r.downloads.calls).toContain('erase:1');
    expect(t.tabs.closed).toEqual([41]);
  });

  it('never logs the cookie or the query of the URL', async () => {
    const t = setup();
    await t.refresh.start(message());
    const url = `${FILE}?token=${CANARY_QUERY}`;
    const claim = t.refresh.claim(t.r.add({ id: 1, url, finalUrl: url }));
    await settle();
    t.r.port().receive(ack('lr1'));
    await claim;
    expect(t.r.allLogs()).not.toContain(CANARY_COOKIE);
    expect(t.r.allLogs()).not.toContain(CANARY_QUERY);
  });
});
