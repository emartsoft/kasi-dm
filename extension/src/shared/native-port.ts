import type { Ack, Hello, Rules } from '../types/protocol';
import { createLogger, type Logger } from './logger';
import { parseAck, parseError, parseRules } from './messages';

export const NATIVE_HOST = 'com.martsoft.kasi';

export interface PortLike {
  postMessage(message: unknown): void;
  disconnect(): void;
  onMessage: { addListener(listener: (message: unknown) => void): void };
  onDisconnect: { addListener(listener: () => void): void };
}

export interface NativePortOptions {
  connect: () => PortLike;
  client: Hello['client'];
  extensionVersion: string;
  onRules?: (rules: Rules) => void;
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

export function createNativePort(options: NativePortOptions) {
  const log = options.logger ?? createLogger();
  const newId = options.newId ?? (() => crypto.randomUUID());
  const pending = new Map<string, Pending>();
  let port: PortLike | undefined;
  let connected = false;
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

  function handle(raw: unknown, current: PortLike): void {
    if (current !== port) return;
    const rules = parseRules(raw);
    const ack = rules ? undefined : parseAck(raw);
    const error = rules || ack ? undefined : parseError(raw);
    if (!rules && !ack && !error) {
      log.warn('Ignored an unrecognised message from Kasi');
      return;
    }
    // Any valid message proves the host is alive, and Kasi answers hello with rules.
    connected = true;
    backoff = BACKOFF_START_MS;
    if (rules) {
      ackTimeoutMs = rules.ackTimeoutMs;
      options.onRules?.(rules);
    } else if (ack) {
      settle(ack.id)?.resolve(ack);
    } else if (error) {
      settle(error.id)?.reject(new NativeRequestError(error.code));
    }
  }

  function scheduleReconnect(): void {
    retry = setTimeout(connect, backoff);
    backoff = Math.min(backoff * 2, BACKOFF_MAX_MS);
  }

  function connect(): void {
    retry = undefined;
    let current: PortLike;
    try {
      current = options.connect();
    } catch (error) {
      log.warn('connectNative failed', { error });
      scheduleReconnect();
      return;
    }
    port = current;
    current.onMessage.addListener((raw) => {
      handle(raw, current);
    });
    current.onDisconnect.addListener(() => {
      if (current !== port) return;
      log.info('Native host disconnected');
      port = undefined;
      connected = false;
      failAll('DISCONNECTED');
      scheduleReconnect();
    });
    const hello: Hello = {
      v: 1,
      id: newId(),
      type: 'hello',
      client: options.client,
      extensionVersion: options.extensionVersion,
      protocolMin: 1,
      protocolMax: 1,
    };
    current.postMessage(hello);
  }

  return {
    start: (): void => {
      if (!port && retry === undefined) connect();
    },
    isConnected: (): boolean => connected,
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
