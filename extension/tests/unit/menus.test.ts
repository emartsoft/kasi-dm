// KASI-BRW-004, plan m2-5 2.4: the context menus. The real port manager runs behind a fake port; every browser API is a fake.
import { readFileSync } from 'node:fs';
import { Ajv } from 'ajv';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  createMenus,
  INSTALLED_KEY,
  MENU_ALL,
  MENU_LINK,
  MENU_SELECTED,
  parseMenuClick,
  type MenuProperties,
} from '../../src/shared/menus';
import type { RawAnchor } from '../../src/shared/link-collector';
import { createLogger } from '../../src/shared/logger';
import { INTERCEPTION_KEY } from '../../src/shared/settings';
import type { Cookie } from '../../src/types/protocol';
import { ack, CANARY_COOKIE, CANARY_QUERY, memoryArea, rig, settle } from './rig';

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

const titles = {
  menu_download_link: 'Download with Kasi',
  menu_download_all: 'Download all links with Kasi',
  menu_download_selected: 'Download selected links with Kasi',
} as const;

const cookie: Cookie = {
  name: 'sid',
  value: CANARY_COOKIE,
  domain: '.example.com',
  path: '/',
  secure: true,
  httpOnly: true,
};

function setup(anchors: RawAnchor[] = []) {
  const r = rig();
  const created: MenuProperties[] = [];
  const events: string[] = [];
  const session = memoryArea();
  const cookieReads: { urls: string[]; incognito: boolean }[] = [];
  const collects: { tabId: number; scope: string }[] = [];
  let disconnected = 0;
  let n = 0;
  const menus = createMenus({
    menus: {
      removeAll: () => {
        events.push('removeAll');
        created.length = 0;
        return Promise.resolve();
      },
      create: (properties) => {
        events.push(`create:${properties.id}`);
        created.push(properties);
      },
    },
    title: (key) => titles[key],
    collect: (tabId, scope) => {
      collects.push({ tabId, scope });
      return Promise.resolve(anchors);
    },
    cookiesFor: (urls, incognito) => {
      cookieReads.push({ urls, incognito });
      return Promise.resolve([cookie]);
    },
    port: r.native,
    session,
    userAgent: 'TestAgent/1.0',
    newId: () => `menu${String(++n)}`,
    onDisconnected: () => {
      disconnected++;
      r.badge.text = '!';
    },
    logger: createLogger({
      debug: (...a: unknown[]) => r.logs.push(a),
      info: (...a: unknown[]) => r.logs.push(a),
      warn: (...a: unknown[]) => r.logs.push(a),
      error: (...a: unknown[]) => r.logs.push(a),
    }),
  });
  return {
    r,
    menus,
    created,
    events,
    session,
    cookieReads,
    collects,
    disconnected: () => disconnected,
    sent: (type: string) => r.port().sent.filter((m) => m.type === type),
  };
}

const anchor = (href: string): RawAnchor => ({ href, attribute: href, download: '' });

describe('[TC-BRW-005] creating the menus', () => {
  it('creates the three menus with their contexts, after removing what was there', async () => {
    const t = setup();

    await t.menus.install(true);

    expect(t.events).toEqual([
      'removeAll',
      `create:${MENU_LINK}`,
      `create:${MENU_ALL}`,
      `create:${MENU_SELECTED}`,
    ]);
    expect(t.created.map((m) => [m.id, m.title, m.contexts])).toEqual([
      [MENU_LINK, 'Download with Kasi', ['link', 'image', 'video', 'audio']],
      [MENU_ALL, 'Download all links with Kasi', ['page']],
      [MENU_SELECTED, 'Download selected links with Kasi', ['selection']],
    ]);
  });

  it('does it once per browser session: a worker restart finds the flag and creates nothing', async () => {
    const t = setup();

    await t.menus.install(false);
    await t.menus.install(false);

    expect(t.events.filter((e) => e.startsWith('create:'))).toHaveLength(3);
    expect(t.session.data[INSTALLED_KEY]).toBe(true);
  });

  it('onInstalled forces a fresh set, and two overlapping installs never duplicate a menu', async () => {
    const t = setup();

    await Promise.all([t.menus.install(false), t.menus.install(true), t.menus.install(false)]);

    // removeAll empties the list each time, so what remains is exactly one set
    expect(t.created.map((m) => m.id)).toEqual([MENU_LINK, MENU_ALL, MENU_SELECTED]);
  });
});

describe('[TC-BRW-004] "Download with Kasi" on a link or media', () => {
  it('sends one valid addDownload for the link, with the page as referrer and cookies read only now', async () => {
    const t = setup();
    t.r.online();
    expect(t.cookieReads).toHaveLength(0);

    const click = t.menus.onClicked(
      {
        menuItemId: MENU_LINK,
        linkUrl: 'https://files.example.com/pack.zip',
        pageUrl: 'https://example.com/downloads',
      },
      { id: 4, incognito: false },
    );
    await settle();
    const [request] = t.sent('addDownload');
    t.r.port().receive(ack(request?.id as string));
    await click;

    expect(request).toMatchObject({
      type: 'addDownload',
      url: 'https://files.example.com/pack.zip',
      finalUrl: 'https://files.example.com/pack.zip',
      referrer: 'https://example.com/downloads',
      filename: 'pack.zip',
      userAgent: 'TestAgent/1.0',
    });
    expect(validate(request)).toBe(true);
    expect(t.cookieReads).toEqual([
      { urls: ['https://files.example.com/pack.zip'], incognito: false },
    ]);
    expect(t.r.downloads.calls).toEqual([]);
  });

  it('uses the source of an image or a video when there is no link', async () => {
    const t = setup();
    t.r.online();

    const click = t.menus.onClicked(
      { menuItemId: MENU_LINK, srcUrl: 'https://cdn.example.com/clip.mp4' },
      { id: 1 },
    );
    await settle();
    t.r.port().receive(ack(t.sent('addDownload')[0]?.id as string));
    await click;

    expect(t.sent('addDownload')[0]).toMatchObject({ url: 'https://cdn.example.com/clip.mp4' });
  });

  it.each([
    'blob:https://example.com/uuid',
    'data:text/plain,hi',
    'javascript:void(0)',
    `https://example.com/${'a'.repeat(8200)}`,
  ])('sends nothing and reads no cookie for %s', async (url) => {
    const t = setup();
    t.r.online();

    await t.menus.onClicked({ menuItemId: MENU_LINK, linkUrl: url }, { id: 1 });

    expect(t.sent('addDownload')).toHaveLength(0);
    expect(t.cookieReads).toHaveLength(0);
  });

  it('reads the private window cookie store for a click in a private window', async () => {
    const t = setup();
    t.r.online();

    const click = t.menus.onClicked(
      { menuItemId: MENU_LINK, linkUrl: 'https://example.com/a.zip' },
      { id: 1, incognito: true },
    );
    await settle();
    t.r.port().receive(ack(t.sent('addDownload')[0]?.id as string));
    await click;

    expect(t.cookieReads[0]?.incognito).toBe(true);
  });

  it('works while sending downloads is switched off: the toggle is not the menus business', async () => {
    const t = setup();
    t.r.local.data[INTERCEPTION_KEY] = false;
    t.r.online();

    const click = t.menus.onClicked(
      { menuItemId: MENU_LINK, linkUrl: 'https://example.com/a.zip' },
      { id: 1 },
    );
    await settle();
    t.r.port().receive(ack(t.sent('addDownload')[0]?.id as string));
    await click;

    expect(t.sent('addDownload')).toHaveLength(1);
  });
});

describe('[TC-BRW-005] "Download all links" and "Download selected links"', () => {
  const forty = Array.from({ length: 40 }, (_, i) =>
    anchor(`https://example.com/docs/doc${String(i)}.pdf`),
  );

  it('sends one valid addBatch with the 40 links, the page as referrer and cookies for the page and the hosts', async () => {
    const t = setup([...forty, anchor('https://cdn.example.net/x.zip')]);
    t.r.online();

    const click = t.menus.onClicked(
      { menuItemId: MENU_ALL, pageUrl: 'https://example.com/downloads' },
      { id: 7, url: 'https://example.com/downloads' },
    );
    await settle();
    const [batch] = t.sent('addBatch');
    t.r.port().receive(ack(batch?.id as string));
    await click;

    expect(t.collects).toEqual([{ tabId: 7, scope: 'all' }]);
    expect(validate(batch)).toBe(true);
    expect(batch).toMatchObject({
      referrer: 'https://example.com/downloads',
      userAgent: 'TestAgent/1.0',
    });
    expect((batch?.links as unknown[]).length).toBe(41);
    expect(t.cookieReads[0]?.urls).toEqual([
      'https://example.com/downloads',
      'https://example.com/docs/doc0.pdf',
      'https://cdn.example.net/x.zip',
    ]);
  });

  it('asks for the selection only for "Download selected links"', async () => {
    const t = setup(forty);
    t.r.online();

    const click = t.menus.onClicked(
      { menuItemId: MENU_SELECTED, pageUrl: 'https://example.com/' },
      { id: 3 },
    );
    await settle();
    t.r.port().receive(ack(t.sent('addBatch')[0]?.id as string));
    await click;

    expect(t.collects).toEqual([{ tabId: 3, scope: 'selection' }]);
  });

  it('sends nothing when the page has no downloadable links, or the click has no tab', async () => {
    const t = setup([anchor('javascript:void(0)'), anchor('mailto:me@example.com')]);
    t.r.online();

    await t.menus.onClicked({ menuItemId: MENU_ALL, pageUrl: 'https://example.com/' }, { id: 1 });
    await t.menus.onClicked({ menuItemId: MENU_ALL, pageUrl: 'https://example.com/' });

    expect(t.sent('addBatch')).toHaveLength(0);
  });

  it('ignores a menu id that is not ours', async () => {
    const t = setup(forty);
    t.r.online();

    await t.menus.onClicked({ menuItemId: 'someone-elses' }, { id: 1 });

    expect(t.collects).toHaveLength(0);
    expect(t.sent('addBatch')).toHaveLength(0);
  });
});

describe('[TC-BRW-006] when Kasi cannot be reached', () => {
  it('shows the disconnected badge at once and sends nothing when no Kasi is installed', async () => {
    const t = setup(
      Array.from({ length: 3 }, (_, i) => anchor(`https://example.com/${String(i)}.zip`)),
    );
    t.r.native.start();
    t.r.port().receive({ v: 1, id: 'x', type: 'error', code: 'APP_NOT_INSTALLED' });

    await t.menus.onClicked({ menuItemId: MENU_ALL, pageUrl: 'https://example.com/' }, { id: 1 });

    expect(t.disconnected()).toBe(1);
    expect(t.r.badge.text).toBe('!');
    expect(t.sent('addBatch')).toHaveLength(0);
  });

  it('waits for Kasi to start up to the ready budget, then sends', async () => {
    const t = setup([anchor('https://example.com/a.zip')]);
    t.r.native.start();
    const click = t.menus.onClicked(
      { menuItemId: MENU_ALL, pageUrl: 'https://example.com/' },
      { id: 1 },
    );
    await settle();
    expect(t.sent('addBatch')).toHaveLength(0);

    await vi.advanceTimersByTimeAsync(3000);
    t.r.port().receive({
      v: 1,
      id: 'r1',
      type: 'rules',
      enabled: true,
      extensions: ['zip'],
      mimeTypes: [],
      excludedSites: [],
      bypassKey: 'alt',
      ackTimeoutMs: 2000,
    });
    await settle();
    t.r.port().receive(ack(t.sent('addBatch')[0]?.id as string));
    await click;

    expect(t.sent('addBatch')).toHaveLength(1);
    expect(t.disconnected()).toBe(0);
  });

  it('shows the badge when Kasi refuses or does not answer', async () => {
    const t = setup([anchor('https://example.com/a.zip')]);
    t.r.online();

    const click = t.menus.onClicked(
      { menuItemId: MENU_ALL, pageUrl: 'https://example.com/' },
      { id: 1 },
    );
    await settle();
    await vi.advanceTimersByTimeAsync(2500); // the 2 s ack window passes
    await click;

    expect(t.disconnected()).toBe(1);
  });
});

describe('[TC-BRW-005] secrets', () => {
  it('never logs a cookie value or a query string, on success or failure', async () => {
    const t = setup([anchor(`https://example.com/a.zip?token=${CANARY_QUERY}`)]);
    t.r.online();

    const ok = t.menus.onClicked(
      { menuItemId: MENU_LINK, linkUrl: `https://example.com/a.zip?token=${CANARY_QUERY}` },
      { id: 1 },
    );
    await settle();
    t.r.port().receive(ack(t.sent('addDownload')[0]?.id as string));
    await ok;
    const failing = t.menus.onClicked(
      { menuItemId: MENU_ALL, pageUrl: `https://example.com/?x=${CANARY_QUERY}` },
      { id: 1 },
    );
    await settle();
    await vi.advanceTimersByTimeAsync(2500);
    await failing;

    const logs = t.r.allLogs();
    expect(logs).not.toContain(CANARY_COOKIE);
    expect(logs).not.toContain(CANARY_QUERY);
  });
});

describe('[TC-BRW-005] the menu click carried as a message (end-to-end suites only)', () => {
  it('accepts exactly our three menu ids and keeps only the known string fields', () => {
    const parsed = parseMenuClick({
      kind: 'kasi-menu-click',
      info: { menuItemId: MENU_ALL, pageUrl: 'https://example.com/', extra: 'dropped', linkUrl: 5 },
      tab: { id: 7, url: 'https://example.com/', incognito: true, other: 1 },
    });

    expect(parsed).toEqual({
      kind: 'kasi-menu-click',
      info: { menuItemId: MENU_ALL, pageUrl: 'https://example.com/' },
      tab: { id: 7, url: 'https://example.com/', incognito: true },
    });
  });

  it('refuses another menu id, a wrong kind and anything that is not an object', () => {
    const tab = { id: 1 };
    expect(
      parseMenuClick({ kind: 'kasi-menu-click', info: { menuItemId: 'other' }, tab }),
    ).toBeUndefined();
    expect(parseMenuClick({ kind: 'x', info: { menuItemId: MENU_ALL }, tab })).toBeUndefined();
    expect(
      parseMenuClick({ kind: 'kasi-menu-click', info: { menuItemId: MENU_ALL } }),
    ).toBeUndefined();
    expect(parseMenuClick('kasi-menu-click')).toBeUndefined();
    expect(parseMenuClick(null)).toBeUndefined();
  });
});
