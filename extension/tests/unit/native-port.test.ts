import { afterEach, beforeEach, describe, expect, it, vi, type Mock } from 'vitest';
import type { Rules } from '../../src/types/protocol';
import { createNativePort, type PortLike } from '../../src/shared/native-port';

class FakePort implements PortLike {
  sent: Record<string, unknown>[] = [];
  private messageListeners: ((m: unknown) => void)[] = [];
  private disconnectListeners: (() => void)[] = [];
  onMessage = { addListener: (l: (m: unknown) => void) => this.messageListeners.push(l) };
  onDisconnect = { addListener: (l: () => void) => this.disconnectListeners.push(l) };
  postMessage(m: unknown) {
    this.sent.push(m as Record<string, unknown>);
  }
  disconnect() {
    this.drop();
  }
  receive(m: unknown) {
    this.messageListeners.forEach((l) => {
      l(m);
    });
  }
  drop() {
    this.disconnectListeners.forEach((l) => {
      l();
    });
  }
}

const rules = {
  v: 1,
  id: 'r1',
  type: 'rules',
  enabled: true,
  extensions: ['zip'],
  mimeTypes: [],
  excludedSites: [],
  bypassKey: 'alt',
  ackTimeoutMs: 2000,
};

describe('native port manager', () => {
  let ports: FakePort[];
  let onRules: Mock<(rules: Rules) => void>;
  let n: number;
  const make = (connect?: () => PortLike) => {
    const manager = createNativePort({
      connect:
        connect ??
        (() => {
          const port = new FakePort();
          ports.push(port);
          return port;
        }),
      client: 'chrome',
      extensionVersion: '0.1.0',
      onRules,
      newId: () => `id${String(++n)}`,
      logger: {
        debug: () => {},
        info: () => {},
        warn: () => {},
        error: () => {},
      },
    });
    manager.start();
    return manager;
  };
  const port = (i: number): FakePort => ports[i] as FakePort;

  beforeEach(() => {
    vi.useFakeTimers();
    ports = [];
    n = 0;
    onRules = vi.fn<(rules: Rules) => void>();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('sends hello on connect and reports rules', () => {
    const manager = make();
    expect(port(0).sent).toEqual([
      {
        v: 1,
        id: 'id1',
        type: 'hello',
        client: 'chrome',
        extensionVersion: '0.1.0',
        protocolMin: 1,
        protocolMax: 1,
      },
    ]);
    expect(manager.isConnected()).toBe(false);
    port(0).receive(rules);
    expect(manager.isConnected()).toBe(true);
    expect(onRules).toHaveBeenCalledWith(rules);
  });

  it('reconnects with doubling backoff, capped, and says hello each time', () => {
    make();
    const waits: number[] = [];
    for (let i = 0; i < 7; i++) {
      port(i).drop();
      const before = ports.length;
      let waited = 0;
      while (ports.length === before) {
        vi.advanceTimersByTime(500);
        waited += 500;
      }
      waits.push(waited);
    }
    expect(waits).toEqual([1000, 2000, 4000, 8000, 16000, 30000, 30000]);
    expect(ports.every((p) => p.sent[0]?.type === 'hello')).toBe(true);
  });

  it('resets the backoff after a message arrives', () => {
    make();
    port(0).drop();
    vi.advanceTimersByTime(1000);
    port(1).receive({ v: 1, id: 'x', type: 'ack', ok: true }); // heard, but Kasi never connected: this is no lost connection
    port(1).drop();
    vi.advanceTimersByTime(999);
    expect(ports).toHaveLength(2);
    vi.advanceTimersByTime(1);
    expect(ports).toHaveLength(3);
  });

  it('retries when connectNative throws', () => {
    let calls = 0;
    make(() => {
      calls++;
      throw new Error('no host');
    });
    vi.advanceTimersByTime(1000);
    expect(calls).toBe(2);
  });

  it('resolves a request with the ack of the same id, in any order', async () => {
    const manager = make();
    port(0).receive(rules);
    const a = manager.request({ id: 'a' });
    const b = manager.request({ id: 'b' });
    expect(
      port(0)
        .sent.slice(1)
        .map((m) => m.id),
    ).toEqual(['a', 'b']);
    port(0).receive({ v: 1, id: 'b', type: 'ack', ok: true });
    port(0).receive({ v: 1, id: 'a', type: 'ack', ok: true });
    expect((await a).id).toBe('a');
    expect((await b).id).toBe('b');
  });

  it('rejects after the 2 s ack timeout', async () => {
    const manager = make();
    port(0).receive(rules);
    const result = manager.request({ id: 'a' });
    const assertion = expect(result).rejects.toMatchObject({ code: 'TIMEOUT' });
    vi.advanceTimersByTime(1999);
    vi.advanceTimersByTime(1);
    await assertion;
  });

  it('rejects with the code of an error reply', async () => {
    const manager = make();
    port(0).receive(rules);
    const result = manager.request({ id: 'a' });
    port(0).receive({ v: 1, id: 'a', type: 'error', code: 'APP_NOT_RUNNING' });
    await expect(result).rejects.toMatchObject({ code: 'APP_NOT_RUNNING' });
  });

  it('rejects pending requests on disconnect and while not connected', async () => {
    const manager = make();
    await expect(manager.request({ id: 'x' })).rejects.toMatchObject({ code: 'NOT_CONNECTED' });
    port(0).receive(rules);
    const result = manager.request({ id: 'a' });
    port(0).drop();
    await expect(result).rejects.toMatchObject({ code: 'DISCONNECTED' });
    expect(manager.isConnected()).toBe(false);
  });

  it('ignores malformed messages', () => {
    const manager = make();
    port(0).receive({ v: 1, id: 'r1', type: 'rules', enabled: 'yes' });
    port(0).receive('nonsense');
    expect(manager.isConnected()).toBe(false);
    expect(onRules).not.toHaveBeenCalled();
  });
});

describe('[TC-BRW-002] native port: connected means Kasi answered', () => {
  const quiet = { debug: () => {}, info: () => {}, warn: () => {}, error: () => {} };
  let made: FakePort[];
  const start = (onLateAck?: (id: string) => void) => {
    made = [];
    const manager = createNativePort({
      connect: () => {
        const p = new FakePort();
        made.push(p);
        return p;
      },
      client: 'chrome',
      extensionVersion: '0.2.0',
      logger: quiet,
      newId: () => `h${String(made.length)}-${String(made[made.length - 1]?.sent.length)}`,
      ...(onLateAck ? { onLateAck } : {}),
    });
    manager.start();
    return manager;
  };
  beforeEach(() => {
    vi.useFakeTimers();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it('is not connected after an ack or an ordinary error, only after rules', () => {
    const manager = start();
    made[0]?.receive({ v: 1, id: 'x', type: 'ack', ok: true });
    made[0]?.receive({ v: 1, id: 'x', type: 'error', code: 'INTERNAL' });
    expect(manager.isConnected()).toBe(false);
    made[0]?.receive(rules);
    expect(manager.isConnected()).toBe(true);
  });

  it.each([
    ['APP_NOT_RUNNING', 'app-not-running'],
    ['APP_NOT_INSTALLED', 'app-not-installed'],
  ])('%s turns connected off and is remembered as %s', async (code, failure) => {
    const manager = start();
    made[0]?.receive(rules);
    const pending = manager.request({ id: 'a' });
    made[0]?.receive({ v: 1, id: 'a', type: 'error', code });
    await expect(pending).rejects.toMatchObject({ code });
    expect(manager.isConnected()).toBe(false);
    expect(manager.lastFailure()).toBe(failure);
    // the host exits after the notice: nothing comes back by itself, a reason (here a download) reconnects on a fresh port
    vi.advanceTimersByTime(60_000);
    expect(made).toHaveLength(1);
    manager.reconnectNow();
    made[1]?.receive(rules);
    expect(manager.isConnected()).toBe(true);
    expect(manager.lastFailure()).toBeUndefined();
  });

  it('says hello again on the same port after an APP_* error, and again on a new port after a disconnect', () => {
    const manager = start();
    made[0]?.receive({ v: 1, id: 'h', type: 'error', code: 'APP_NOT_RUNNING' });
    vi.advanceTimersByTime(1000);
    expect(made).toHaveLength(1);
    expect(made[0]?.sent.map((m) => m.type)).toEqual(['hello', 'hello']);
    made[0]?.drop();
    vi.advanceTimersByTime(2000);
    expect(made).toHaveLength(2);
    expect(made[1]?.sent[0]?.type).toBe('hello');
    expect(manager.isConnected()).toBe(false);
  });

  it('reports a host that died before saying anything as missing, and a silent connect failure too', () => {
    const manager = start();
    made[0]?.drop();
    expect(manager.lastFailure()).toBe('host-missing');
    const thrower = createNativePort({
      connect: () => {
        throw new Error('no host');
      },
      client: 'chrome',
      extensionVersion: '0.2.0',
      logger: quiet,
    });
    thrower.start();
    expect(thrower.lastFailure()).toBe('host-missing');
  });

  it('whenConnected resolves on rules, expires on time, and answers false at once when nothing can answer', async () => {
    const manager = start();
    const ok = manager.whenConnected(6000);
    made[0]?.receive(rules);
    await expect(ok).resolves.toBe(true);
    await expect(manager.whenConnected(1)).resolves.toBe(true);

    const slow = start();
    const late = slow.whenConnected(6000);
    vi.advanceTimersByTime(5999);
    vi.advanceTimersByTime(1);
    await expect(late).resolves.toBe(false);

    const missing = start();
    made[0]?.drop();
    await expect(missing.whenConnected(6000)).resolves.toBe(false);
  });

  it('whenConnected gives up when the host reports Kasi is not running', async () => {
    const manager = start();
    const waiting = manager.whenConnected(6000);
    made[0]?.receive({ v: 1, id: 'h', type: 'error', code: 'APP_NOT_RUNNING' });
    await expect(waiting).resolves.toBe(false);
  });

  it('reconnectNow skips the backoff, but does not repeat a hello that is still in flight', () => {
    const manager = start();
    manager.reconnectNow();
    expect(made[0]?.sent).toHaveLength(1);
    made[0]?.drop();
    manager.reconnectNow();
    expect(made).toHaveLength(2);
    made[1]?.receive(rules);
    manager.reconnectNow();
    expect(made[1]?.sent).toHaveLength(1);
  });

  it('reports an ack nobody waits for, so a late ack can be acted on', async () => {
    const late = vi.fn<(id: string) => void>();
    const manager = start(late);
    made[0]?.receive(rules);
    const pending = manager.request({ id: 'a' });
    const assertion = expect(pending).rejects.toMatchObject({ code: 'TIMEOUT' });
    vi.advanceTimersByTime(2000);
    await assertion;
    made[0]?.receive({ v: 1, id: 'a', type: 'ack', ok: true });
    expect(late).toHaveBeenCalledWith('a');
  });

  it('hands a refreshLink from Kasi to its listener, and ignores a malformed one', () => {
    const seen: string[] = [];
    const manager = createNativePort({
      connect: () => {
        const p = new FakePort();
        made.push(p);
        return p;
      },
      client: 'chrome',
      extensionVersion: '0.2.0',
      logger: quiet,
      onRefreshLink: (m) => seen.push(m.jobId),
    });
    made = [];
    manager.start();
    made[0]?.receive(rules);
    made[0]?.receive({
      v: 1,
      id: 'k',
      type: 'refreshLink',
      jobId: 'j1',
      referrerUrl: 'https://a.example.org/p',
    });
    made[0]?.receive({
      v: 1,
      id: 'k',
      type: 'refreshLink',
      jobId: 'j2',
      referrerUrl: 'file:///etc/passwd',
    });
    expect(seen).toEqual(['j1']);
  });

  it('keeps requests in the order they were made', () => {
    const manager = start();
    made[0]?.receive(rules);
    void manager.request({ id: 'first' }).catch(() => undefined);
    void manager.request({ id: 'second' }).catch(() => undefined);
    expect(made[0]?.sent.slice(1).map((m) => m.id)).toEqual(['first', 'second']);
  });
});
