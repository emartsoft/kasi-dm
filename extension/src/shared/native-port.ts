import type { Ack, Hello, RefreshLink, Rules } from '../types/protocol';
import { createLogger, type Logger } from './logger';
import { parseAck, parseError, parseRefreshLink, parseRules } from './messages';

export const NATIVE_HOST = 'com.martsoft.kasi';

export interface PortLike {
  postMessage(message: unknown): void;
  disconnect(): void;
  onMessage: { addListener(listener: (message: unknown) => void): void };
  onDisconnect: { addListener(listener: () => void): void };
}

// Why Kasi is unreachable, as far as the host or the browser told us. `host-missing`: the port died before the host said anything, so nothing is registered.
export type PortFailure = 'host-missing' | 'app-not-installed' | 'app-not-running';

export interface NativePortOptions {
  connect: () => PortLike;
  client: Hello['client'];
  extensionVersion: string;
  onRules?: (rules: Rules) => void;
  // An ack nobody is waiting for any more, normally for a request that timed out (plan 2.3).
  onLateAck?: (id: string) => void;
  // A connection that was up is gone (Kasi quit, or the port died). Called in the same tick, before anything else reacts.
  onLost?: () => void;
  // Kasi wants a fresh link for a paused job (plan m2-5 2.5). Only a connected Kasi can say so.
  onRefreshLink?: (message: RefreshLink) => void;
  logger?: Logger;
  newId?: () => string;
}

export class NativeRequestError extends Error {
  constructor(readonly code: string) {
    super(code);
    this.name = 'NativeRequestError';
  }
}

interface Pending {
  resolve: (ack: Ack) => void;
  reject: (error: Error) => void;
  timer: ReturnType<typeof setTimeout>;
}

const BACKOFF_START_MS = 1000;
const BACKOFF_MAX_MS = 30000;

function failureOf(code: string): PortFailure | undefined {
  if (code === 'APP_NOT_RUNNING') return 'app-not-running';
  return code === 'APP_NOT_INSTALLED' ? 'app-not-installed' : undefined;
}

export function createNativePort(options: NativePortOptions) {
  const log = options.logger ?? createLogger();
  const newId = options.newId ?? (() => crypto.randomUUID());
  const pending = new Map<string, Pending>();
  const waiters = new Set<(ok: boolean) => void>();
  let port: PortLike | undefined;
  let connected = false;
  // Has the current port said anything yet? A port that dies silent is a host that is not there.
  let heard = false;
  let lastFailure: PortFailure | undefined;
  // After a lost connection nothing reconnects on a timer: Kasi quit, and a timer would start it again (plan m2-5 decision 3).
  // A download, the popup or a browser start is the reason to try again.
  let lost = false;
  let backoff = BACKOFF_START_MS;
  let retry: ReturnType<typeof setTimeout> | undefined;
  let ackTimeoutMs = 2000;

  function failAll(code: string): void {
    for (const entry of pending.values()) {
      clearTimeout(entry.timer);
      entry.reject(new NativeRequestError(code));
    }
    pending.clear();
  }

  function settle(id: string): Pending | undefined {
    const entry = pending.get(id);
    if (entry) {
      clearTimeout(entry.timer);
      pending.delete(id);
    }
    return entry;
  }

  function wake(ok: boolean): void {
    for (const waiter of [...waiters]) waiter(ok);
  }

  // The connection that was up is gone; the caller says whether the port is still open (an APP_* notice) or already dead.
  function markLost(): void {
    lost = true;
    options.onLost?.();
  }

  function handle(raw: unknown, current: PortLike): void {
    if (current !== port) return;
    const refresh = parseRefreshLink(raw);
    if (refresh) {
      heard = true;
      options.onRefreshLink?.(refresh);
      return;
    }
    const rules = parseRules(raw);
    const ack = rules ? undefined : parseAck(raw);
    const error = rules || ack ? undefined : parseError(raw);
    if (!rules && !ack && !error) {
      log.warn('Ignored an unrecognised message from Kasi');
      return;
    }
    heard = true;
    backoff = BACKOFF_START_MS;
    // State is settled before any callback runs, so a listener sees the new truth.
    // The host answers APP_* with the id of the message it could not deliver and stays up; only `rules` proves Kasi is alive (BRW-006).
    const failure = error ? failureOf(error.code) : undefined;
    if (rules) {
      connected = true;
      lost = false;
      lastFailure = undefined;
      ackTimeoutMs = rules.ackTimeoutMs;
      options.onRules?.(rules);
      wake(true);
    } else if (failure) {
      const wasConnected = connected;
      connected = false;
      lastFailure = failure;
      settle(error?.id ?? '')?.reject(new NativeRequestError(error?.code ?? 'INTERNAL'));
      failAll('DISCONNECTED');
      if (wasConnected) {
        // The host exits after this notice; let go of its port so the next connect is a fresh host.
        port = undefined;
        current.disconnect();
        markLost();
      } else {
        scheduleReconnect();
      }
      wake(false);
    } else if (ack) {
      const entry = settle(ack.id);
      if (entry) entry.resolve(ack);
      else options.onLateAck?.(ack.id);
    } else if (error) {
      settle(error.id)?.reject(new NativeRequestError(error.code));
    }
  }

  function scheduleReconnect(): void {
    if (retry !== undefined) clearTimeout(retry);
    retry = setTimeout(connect, backoff);
    backoff = Math.min(backoff * 2, BACKOFF_MAX_MS);
  }

  function hello(): Hello {
    return {
      v: 1,
      id: newId(),
      type: 'hello',
      client: options.client,
      extensionVersion: options.extensionVersion,
      protocolMin: 1,
      protocolMax: 1,
    };
  }

  function connect(): void {
    retry = undefined;
    lost = false;
    // The host outlives an APP_* error and tries Kasi again on the next message, so a retry is a fresh hello on the same port.
    if (port) {
      port.postMessage(hello());
      return;
    }
    let current: PortLike;
    try {
      current = options.connect();
    } catch (error) {
      log.warn('connectNative failed', { error });
      lastFailure = 'host-missing';
      scheduleReconnect();
      return;
    }
    port = current;
    heard = false;
    current.onMessage.addListener((raw) => {
      handle(raw, current);
    });
    current.onDisconnect.addListener(() => {
      if (current !== port) return;
      log.info('Native host disconnected');
      const wasConnected = connected;
      port = undefined;
      connected = false;
      if (!heard) lastFailure = 'host-missing';
      failAll('DISCONNECTED');
      if (wasConnected) markLost();
      if (!lost) scheduleReconnect();
      wake(false);
    });
    current.postMessage(hello());
  }

  // A download is waiting for Kasi: skip the remaining backoff. A hello already in flight (the host may be launching Kasi) is not repeated.
  function reconnectNow(): void {
    if (connected || (port && !heard)) return;
    backoff = BACKOFF_START_MS;
    connect();
  }

  return {
    start: (): void => {
      if (!port && retry === undefined) connect();
    },
    isConnected: (): boolean => connected,
    lastFailure: (): PortFailure | undefined => lastFailure,
    ackTimeoutMs: (): number => ackTimeoutMs,
    reconnectNow,
    // The popup is open: try again unless the user quit Kasi (opening the popup must not bring it back).
    reconnectForStatus: (): void => {
      if (lastFailure !== 'app-not-running') reconnectNow();
    },
    // True once Kasi has sent rules; false on timeout, or at once when no Kasi can answer (nothing registered, or not installed).
    whenConnected: (timeoutMs: number): Promise<boolean> => {
      if (connected) return Promise.resolve(true);
      if (lastFailure === 'host-missing' || lastFailure === 'app-not-installed') {
        return Promise.resolve(false);
      }
      return new Promise<boolean>((resolve) => {
        const done = (ok: boolean): void => {
          clearTimeout(timer);
          waiters.delete(done);
          resolve(ok);
        };
        const timer = setTimeout(() => {
          done(false);
        }, timeoutMs);
        waiters.add(done);
      });
    },
    // Resolves with the ack for message.id; rejects on an error reply, a disconnect, or an ack timeout.
    request: (message: { id: string }): Promise<Ack> => {
      const target = port;
      if (!target || !connected) return Promise.reject(new NativeRequestError('NOT_CONNECTED'));
      return new Promise<Ack>((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(message.id);
          reject(new NativeRequestError('TIMEOUT'));
        }, ackTimeoutMs);
        pending.set(message.id, { resolve, reject, timer });
        target.postMessage(message);
      });
    },
  };
}
