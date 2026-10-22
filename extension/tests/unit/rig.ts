// Shared fakes for the interception suites: a fake browser API (downloads, cookies, storage, badge) around the real modules,
// and a fake native port behind the real port manager. No browser and no network.
import { createBypass } from '../../src/shared/bypass';
import { createInterception, type DownloadItemLike } from '../../src/shared/interception';
import { createLogger } from '../../src/shared/logger';
import { createNativePort, type PortLike } from '../../src/shared/native-port';
import { createRequestTracker } from '../../src/shared/request-tracker';
import {
  createRulesCache,
  createRulesProvider,
  type StorageAreaLike,
} from '../../src/shared/rules-cache';
import { collectCookies, storeFor, type CookiesApi } from '../../src/shared/cookies';
import type { AddDownload, Cookie, Rules } from '../../src/types/protocol';

export const CANARY_COOKIE = 'CANARY-COOKIE-VALUE-9f3a';
export const CANARY_QUERY = 'CANARY-QUERY-71bc';

export class FakePort implements PortLike {
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
  requests(): AddDownload[] {
    return this.sent.filter((m) => m.type === 'addDownload') as unknown as AddDownload[];
  }
}

export function memoryArea(): StorageAreaLike & { data: Record<string, unknown> } {
  const data: Record<string, unknown> = {};
  return {
    data,
    get: (key) => Promise.resolve(key in data ? { [key]: data[key] } : {}),
    set: (items) => {
      Object.assign(data, items);
      return Promise.resolve();
    },
  };
}

export const rules: Rules = {
  v: 1,
  id: 'r1',
  type: 'rules',
  enabled: true,
  extensions: ['zip', 'iso'],
  mimeTypes: ['application/zip'],
  excludedSites: ['bank.example.com'],
  bypassKey: 'alt',
  ackTimeoutMs: 2000,
};

export const ack = (id: string) => ({ v: 1, id, type: 'ack', ok: true });

export function download(over: Partial<DownloadItemLike> = {}): DownloadItemLike {
  return {
    id: 1,
    url: 'https://files.example.org/pack.zip',
    finalUrl: 'https://files.example.org/pack.zip',
    referrer: 'https://files.example.org/index.html',
    filename: 'C:\\Users\\me\\Downloads\\pack.zip',
    mime: 'application/zip',
    totalBytes: 1000,
    state: 'in_progress',
    paused: false,
    incognito: false,
    ...over,
  };
}

class FakeDownloads {
  items = new Map<number, DownloadItemLike>();
  calls: string[] = [];
  failPause = false;
  pause = (id: number) => {
    this.calls.push(`pause:${String(id)}`);
    const item = this.items.get(id);
    if (this.failPause || item?.state !== 'in_progress') return Promise.reject(new Error('no'));
    item.paused = true;
    return Promise.resolve();
  };
  resume = (id: number) => {
    this.calls.push(`resume:${String(id)}`);
    const item = this.items.get(id);
    if (item) item.paused = false;
    return Promise.resolve();
  };
  cancel = (id: number) => {
    this.calls.push(`cancel:${String(id)}`);
    const item = this.items.get(id);
    if (item) item.state = 'interrupted';
    return Promise.resolve();
  };
  erase = ({ id }: { id: number }) => {
    this.calls.push(`erase:${String(id)}`);
    this.items.delete(id);
    return Promise.resolve();
  };
  removeFile = (id: number) => {
    this.calls.push(`removeFile:${String(id)}`);
    return Promise.resolve();
  };
  search = ({ id }: { id: number }) => {
    const item = this.items.get(id);
    return Promise.resolve(item ? [{ ...item }] : []);
  };
}

export interface RigOptions {
  firefox?: boolean;
  incognitoAllowed?: boolean;
  hostPermission?: boolean;
  cookies?: Record<string, Cookie[]>;
  cachedRules?: Rules;
  realGrace?: boolean;
}

// Flush promise chains that do not depend on timers.
export async function settle(): Promise<void> {
  for (let i = 0; i < 300; i++) await Promise.resolve();
}

export function rig(options: RigOptions = {}) {
  const ports: FakePort[] = [];
  const logs: unknown[][] = [];
  const sink = {
    debug: (...a: unknown[]) => logs.push(a),
    info: (...a: unknown[]) => logs.push(a),
    warn: (...a: unknown[]) => logs.push(a),
    error: (...a: unknown[]) => logs.push(a),
  };
  const logger = createLogger(sink);
  const downloads = new FakeDownloads();
  const badge = { text: '' };
  const local = memoryArea();
  const session = memoryArea();
  if (options.cachedRules) local.data.rules = options.cachedRules;
  const provider = createRulesProvider(createRulesCache(local));
  const tracker = createRequestTracker(session);
  // The 100 ms grace is skipped unless a suite is about it (then it runs on the fake timers).
  const bypass = createBypass({
    area: session,
    ...(options.realGrace ? {} : { sleep: () => Promise.resolve() }),
  });
  const cookieCalls: { url: string; storeId?: string }[] = [];
  const cookieApi: CookiesApi = {
    getAll: (details) => {
      cookieCalls.push(details);
      return Promise.resolve(options.cookies?.[details.url] ?? []);
    },
    getAllCookieStores: () => Promise.resolve([{ id: '0' }, { id: 'private-1' }]),
  };
  let n = 0;
  // eslint-disable-next-line prefer-const -- the port needs `interception` for late acks, and interception needs the port
  let interception: ReturnType<typeof createInterception>;
  const native = createNativePort({
    connect: () => {
      const port = new FakePort();
      ports.push(port);
      return port;
    },
    client: options.firefox ? 'firefox' : 'chrome',
    extensionVersion: '0.2.0',
    logger,
    newId: () => `id${String(++n)}`,
    onRules: (r) => {
      provider.set(r);
      badge.text = '';
    },
    onLateAck: (id) => void interception.lateAck(id),
  });
  interception = createInterception({
    downloads: {
      pause: (id) => downloads.pause(id),
      resume: (id) => downloads.resume(id),
      cancel: (id) => downloads.cancel(id),
      erase: (query) => downloads.erase(query),
      search: (query) => downloads.search(query),
      removeFile: (id) => downloads.removeFile(id),
    },
    port: native,
    getRules: provider.get,
    isIncognitoAllowed: () => Promise.resolve(options.incognitoAllowed ?? false),
    hasHostPermission: () => Promise.resolve(options.hostPermission ?? true),
    isPost: tracker.isPost,
    hops: tracker.hops,
    bypassHeld: bypass.check,
    cookiesFor: async (item) =>
      collectCookies(
        cookieApi,
        [item.url, item.finalUrl ?? item.url],
        await storeFor(cookieApi, item.incognito === true),
      ),
    userAgent: 'TestAgent/1.0',
    newId: () => `req${String(++n)}`,
    onDisconnected: () => {
      badge.text = '!';
    },
    logger,
  });

  return {
    ports,
    logs,
    downloads,
    badge,
    tracker,
    bypass,
    native,
    cookieCalls,
    port: (i = 0) => ports[i] as FakePort,
    /** The port connected and Kasi answered hello with rules. */
    online: (r: Rules = rules) => {
      native.start();
      ports[0]?.receive(r);
    },
    add: (over: Partial<DownloadItemLike> = {}) => {
      const item = download(over);
      downloads.items.set(item.id, item);
      return item;
    },
    /** Starts the flow without waiting for it to end; follow with settle() or fake-timer advances. */
    start: (item: DownloadItemLike) => interception.handle(item),
    allLogs: () => JSON.stringify(logs),
  };
}
