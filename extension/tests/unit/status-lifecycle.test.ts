// Plan m2-5 section 2.1 and 2.2: Kasi quitting is shown at once and never undone by a timer; the toggle.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { INTERCEPTION_KEY } from '../../src/shared/settings';
import { ack, rig, rules, settle } from './rig';

vi.mock('../../src/shared/hash', () => import('./hash-mock'));

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

const notRunning = { v: 1, id: 'unknown', type: 'error', code: 'APP_NOT_RUNNING' };

describe('[TC-BRW-002] Kasi quits while connected', () => {
  it('an unsolicited APP_NOT_RUNNING marks disconnected and sets the badge in the same tick (the 5 s promise)', () => {
    const r = rig();
    r.online();
    expect(r.native.isConnected()).toBe(true);

    r.port().receive(notRunning);

    // no timer advanced, no promise flushed: it all happened inside the notice
    expect(r.native.isConnected()).toBe(false);
    expect(r.native.lastFailure()).toBe('app-not-running');
    expect(r.badge.text).toBe('!');
  });

  it('the port dying while connected also sets the badge at once', () => {
    const r = rig();
    r.online();

    r.port().drop();

    expect(r.native.isConnected()).toBe(false);
    expect(r.badge.text).toBe('!');
  });

  it.each([
    [
      'the notice',
      (r: ReturnType<typeof rig>) => {
        r.port().receive(notRunning);
      },
    ],
    [
      'the port death',
      (r: ReturnType<typeof rig>) => {
        r.port().drop();
      },
    ],
  ])('does not reconnect on a timer after %s', (_name, lose) => {
    const r = rig();
    r.online();
    lose(r);
    // the host exits after the notice, so its port dies too
    r.port().drop();

    vi.advanceTimersByTime(10 * 60_000);

    expect(r.ports).toHaveLength(1);
    expect(r.native.isConnected()).toBe(false);
  });

  it('a download is a reason: it reconnects, waits for Kasi and hands the download over', async () => {
    const r = rig();
    r.online();
    r.port().receive(notRunning);
    r.port().drop();

    const done = r.start(r.add());
    await settle();
    expect(r.ports).toHaveLength(2);
    expect(r.port(1).sent[0]?.type).toBe('hello');
    r.port(1).receive(rules);
    await settle();
    const [request] = r.port(1).requests();
    r.port(1).receive(ack(request?.id ?? ''));
    await done;

    expect(r.downloads.calls).toEqual(['pause:1', 'cancel:1', 'removeFile:1', 'erase:1']);
    expect(r.badge.text).toBe('');
  });

  it('the popup is a reason when the port died, but not when the user quit Kasi', () => {
    const died = rig();
    died.online();
    died.port().drop();
    died.native.reconnectForStatus();
    expect(died.ports).toHaveLength(2);

    const quit = rig();
    quit.online();
    quit.port().receive(notRunning);
    quit.native.reconnectForStatus();
    expect(quit.ports).toHaveLength(1);
  });

  it('a browser start is a reason: start() connects', () => {
    const r = rig();
    r.native.start();
    expect(r.ports).toHaveLength(1);
  });

  it('rules clear the badge again once Kasi is back', () => {
    const r = rig();
    r.online();
    r.port().receive(notRunning);
    r.native.reconnectNow();
    r.port(1).receive(rules);
    expect(r.native.isConnected()).toBe(true);
    expect(r.badge.text).toBe('');
  });
});

describe('interception toggle', () => {
  it('off: a matching download is left alone, with no pause and no request', async () => {
    const r = rig();
    r.online();
    r.local.data[INTERCEPTION_KEY] = false;

    await r.start(r.add());

    expect(r.downloads.calls).toEqual([]);
    expect(r.port().requests()).toEqual([]);
  });

  it('on (and absent) intercepts as before', async () => {
    const r = rig();
    r.online();
    r.local.data[INTERCEPTION_KEY] = true;

    void r.start(r.add());
    await settle();

    expect(r.downloads.calls).toEqual(['pause:1']);
    expect(r.port().requests()).toHaveLength(1);
  });

  it('off: claims still run and can take the download', async () => {
    const claim = vi.fn((): boolean => true);
    const r = rig({ claims: [claim] });
    r.online();
    r.local.data[INTERCEPTION_KEY] = false;

    await r.start(r.add());

    expect(claim).toHaveBeenCalledTimes(1);
    expect(r.downloads.calls).toEqual([]);
  });
});
