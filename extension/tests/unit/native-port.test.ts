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
    port(1).receive(rules);
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
