import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  GRACE_MS,
  LINK_FRESH_MS,
  OTHER_FRESH_MS,
  createBypass,
  parseModifierMessage,
  type ModifierMessage,
} from '../../src/shared/bypass';
import { installModifierReporter, toModifierMessage } from '../../src/content/modifier';
import { ack, memoryArea, rig, rules, settle } from './rig';

vi.mock('../../src/shared/hash', () => import('./hash-mock'));

const LINK = 'https://files.example.org/pack.zip';
const held = (mods: Partial<ModifierMessage['mods']>, href?: string): ModifierMessage => ({
  kind: 'kasi-modifier',
  mods: { alt: false, ctrl: false, shift: false, ...mods },
  ...(href ? { href } : {}),
});

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

async function bypassed(r: ReturnType<typeof rig>) {
  await r.start(r.add());
  expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
  expect(r.cookieCalls).toEqual([]);
  expect(r.port().requests()).toHaveLength(0);
}

async function intercepted(r: ReturnType<typeof rig>) {
  const done = r.start(r.add());
  await settle();
  const [request] = r.port().requests();
  expect(request).toBeDefined();
  r.port().receive(ack(request?.id ?? ''));
  await done;
}

describe('[TC-BRW-003] the bypass key keeps the browser download', () => {
  it.each([
    ['alt', { alt: true }],
    ['ctrl', { ctrl: true }],
    ['shift', { shift: true }],
  ] as const)('%s held on the link lets the browser download it', async (key, mods) => {
    const r = rig();
    r.online({ ...rules, bypassKey: key });
    await r.bypass.record(held(mods, LINK));
    await bypassed(r);
    expect(r.badge.text).toBe('');
  });

  it('is off when the key is none', async () => {
    const r = rig();
    r.online({ ...rules, bypassKey: 'none' });
    await r.bypass.record(held({ alt: true, ctrl: true, shift: true }, LINK));
    await intercepted(r);
  });

  it('ignores a modifier that is not the configured key', async () => {
    const r = rig();
    r.online({ ...rules, bypassKey: 'alt' });
    await r.bypass.record(held({ shift: true }, LINK));
    await intercepted(r);
  });

  it('honours the link for 15 s and any other click for 3 s', async () => {
    const r = rig();
    r.online();
    await r.bypass.record(held({ alt: true }, LINK));
    await vi.advanceTimersByTimeAsync(LINK_FRESH_MS);
    await bypassed(r);

    const other = rig();
    other.online();
    await other.bypass.record(held({ alt: true }, 'https://elsewhere.example.org/page'));
    await vi.advanceTimersByTimeAsync(OTHER_FRESH_MS);
    await bypassed(other);
  });

  it('lets a stale record go: past 15 s for the link, past 3 s for another page or none', async () => {
    const late = rig();
    late.online();
    await late.bypass.record(held({ alt: true }, LINK));
    await vi.advanceTimersByTimeAsync(LINK_FRESH_MS + 1);
    await intercepted(late);

    const unrelated = rig();
    unrelated.online();
    await unrelated.bypass.record(held({ alt: true }, 'https://elsewhere.example.org/x'));
    await vi.advanceTimersByTimeAsync(OTHER_FRESH_MS + 1);
    await intercepted(unrelated);

    const noLink = rig();
    noLink.online();
    await noLink.bypass.record(held({ alt: true }));
    await vi.advanceTimersByTimeAsync(OTHER_FRESH_MS + 1);
    await intercepted(noLink);
  });

  it('matches the link through a redirect hop and ignores a fragment', async () => {
    const r = rig();
    r.online();
    await r.tracker.onRedirect('https://files.example.org/go?id=1', LINK);
    await r.bypass.record(held({ alt: true }, 'https://files.example.org/go?id=1#top'));
    await vi.advanceTimersByTimeAsync(OTHER_FRESH_MS + 1); // only the link match keeps it this long
    await bypassed(r);
  });

  it('waits a short grace for a modifier message that is still on its way', async () => {
    const r = rig({ realGrace: true });
    r.online();
    const done = r.start(r.add());
    await settle();
    expect(r.downloads.calls).toEqual(['pause:1']);
    await vi.advanceTimersByTimeAsync(GRACE_MS / 2);
    await r.bypass.record(held({ alt: true }, LINK)); // arrives during the grace
    await vi.advanceTimersByTimeAsync(GRACE_MS);
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
    expect(r.port().requests()).toHaveLength(0);
  });

  it('looks for the redirect hop again after the grace: Firefox reports it just after the download is created', async () => {
    const r = rig({ realGrace: true });
    r.online();
    await r.bypass.record(held({ alt: true }, 'https://files.example.org/slow?id=1'));
    await vi.advanceTimersByTimeAsync(OTHER_FRESH_MS + 20); // the slow server answered its 302 only now
    const done = r.start(r.add());
    await settle();
    expect(r.downloads.calls).toEqual(['pause:1']);
    await r.tracker.onRedirect('https://files.example.org/slow?id=1', LINK); // webRequest is a few ms behind downloads
    await vi.advanceTimersByTimeAsync(GRACE_MS);
    await done;
    expect(r.downloads.calls).toEqual(['pause:1', 'resume:1']);
    expect(r.port().requests()).toHaveLength(0);
  });

  it('intercepts when nothing arrives within the grace', async () => {
    const r = rig({ realGrace: true });
    r.online();
    const done = r.start(r.add());
    await vi.advanceTimersByTimeAsync(GRACE_MS);
    await settle();
    const [request] = r.port().requests();
    expect(request).toBeDefined();
    r.port().receive(ack(request?.id ?? ''));
    await done;
  });

  it('reads the record back from storage after the worker restarted', async () => {
    const area = memoryArea();
    await createBypass({ area }).record(held({ alt: true }, LINK));
    const restarted = createBypass({ area, sleep: () => Promise.resolve() });
    expect(await restarted.check('alt', () => Promise.resolve([]))).toBe(true);
  });
});

describe('[TC-BRW-003] the content script reports modifier clicks', () => {
  const anchor = {
    closest: (selector: string) => (selector === 'a[href]' ? { href: LINK } : null),
  };
  const event = (over: Record<string, unknown>) => ({
    type: 'click',
    isTrusted: true,
    altKey: false,
    ctrlKey: false,
    shiftKey: false,
    target: anchor,
    ...over,
  });

  it('sends the held keys and the link of the closest anchor', () => {
    expect(toModifierMessage(event({ altKey: true }))).toEqual({
      kind: 'kasi-modifier',
      mods: { alt: true, ctrl: false, shift: false },
      href: LINK,
    });
  });

  it('sends nothing for a plain click', () => {
    expect(toModifierMessage(event({}))).toBeUndefined();
  });

  it('ignores an event a page script made', () => {
    expect(toModifierMessage(event({ altKey: true, isTrusted: false }))).toBeUndefined();
  });

  it('sends no href when the target is not in a link', () => {
    expect(toModifierMessage(event({ shiftKey: true, target: { closest: () => null } }))).toEqual({
      kind: 'kasi-modifier',
      mods: { alt: false, ctrl: false, shift: true },
    });
  });

  it('counts Enter on a focused link and no other key', () => {
    expect(
      toModifierMessage(event({ type: 'keydown', key: 'Enter', ctrlKey: true })),
    ).toBeDefined();
    expect(toModifierMessage(event({ type: 'keydown', key: 'a', ctrlKey: true }))).toBeUndefined();
  });

  it('listens in the capture phase for pointerdown, click, auxclick and keydown', () => {
    const added: [string, boolean][] = [];
    const sent: ModifierMessage[] = [];
    let handler: ((e: never) => void) | undefined;
    installModifierReporter(
      {
        addEventListener: (type, listener, options) => {
          added.push([type, options.capture]);
          handler = listener;
        },
      },
      (m) => sent.push(m),
    );
    expect(added).toEqual([
      ['pointerdown', true],
      ['click', true],
      ['auxclick', true],
      ['keydown', true],
    ]);
    handler?.(event({ ctrlKey: true }) as never);
    expect(sent).toHaveLength(1);
  });

  it('is parsed strictly by the background', () => {
    expect(parseModifierMessage(held({ alt: true }, LINK))).toBeDefined();
    expect(parseModifierMessage({ kind: 'kasi-modifier', mods: { alt: 1 } })).toBeUndefined();
    expect(parseModifierMessage({ kind: 'other', mods: {} })).toBeUndefined();
    expect(parseModifierMessage(held({}, 'x'.repeat(9000)))).toBeUndefined();
  });
});
