import { describe, expect, it } from 'vitest';
import {
  createMediaDetector,
  parseMediaRuntimeMessage,
  type MediaDetectorDeps,
} from '../../src/shared/media-controller';
import { createMediaSession, type SessionArea } from '../../src/shared/media-session';

class FakeArea implements SessionArea {
  data: Record<string, unknown> = {};

  get(keys: string | string[]): Promise<Record<string, unknown>> {
    const wanted = Array.isArray(keys) ? keys : [keys];
    return Promise.resolve(
      Object.fromEntries(
        wanted.filter((key) => key in this.data).map((key) => [key, this.data[key]]),
      ),
    );
  }

  set(items: Record<string, unknown>): Promise<void> {
    this.data = { ...this.data, ...items };
    return Promise.resolve();
  }
}

const details = (url: string, tabId = 3) => ({
  tabId,
  url,
  statusCode: 200,
  documentUrl: 'https://page.example/watch',
});

function rig() {
  const area = new FakeArea();
  const store = createMediaSession(area);
  const badges: { tabId: number; count: number }[] = [];
  const cleared: number[] = [];
  const sent: unknown[] = [];
  const requested: unknown[] = [];
  const detected: unknown[] = [];
  let id = 0;
  const deps: MediaDetectorDeps = {
    store,
    badge: {
      set: (tabId, count) => {
        badges.push({ tabId, count });
      },
      clear: (tabId) => {
        cleared.push(tabId);
      },
    },
    tabs: {
      sendMessage: (_tabId, message) => {
        sent.push(message);
        return Promise.resolve();
      },
    },
    port: {
      request: (message) => {
        requested.push(message);
        return Promise.resolve({ ok: true });
      },
    },
    cookiesFor: () =>
      Promise.resolve([
        {
          name: 'sid',
          value: 'secret',
          domain: '.example',
          path: '/',
          secure: true,
          httpOnly: true,
        },
      ]),
    userAgent: 'TestAgent/1.0',
    onDetected: (detection) => {
      detected.push(detection);
      return Promise.resolve();
    },
    now: () => 10,
    newId: () => `id-${String(++id)}`,
    logger: { info: () => undefined, error: () => undefined },
  };
  return { detector: createMediaDetector(deps), badges, cleared, sent, requested, detected, store };
}

describe('media detector', () => {
  it('adds a manifest once, paints the tab badge and tells the content script', async () => {
    const { detector, badges, sent, detected } = rig();
    await detector.onResponse(details('https://cdn.example/master.m3u8'));
    await detector.onResponse(details('https://cdn.example/master.m3u8'));

    expect(await detector.list(3)).toHaveLength(1);
    expect(badges.at(-1)).toEqual({ tabId: 3, count: 1 });
    expect(sent).toContainEqual({ type: 'mediaChanged' });
    expect(detected).toHaveLength(1);
    expect((sent as { type: string }[]).every((message) => message.type === 'mediaChanged')).toBe(
      true,
    );
  });

  it('captures the newest detection with the page context and cookies', async () => {
    const { detector, store, requested } = rig();
    await store.setContext(3, { pageUrl: 'https://page.example/', title: 'Clip' });
    await detector.onResponse(details('https://cdn.example/master.m3u8'));

    const capture = await detector.capture(3);
    expect(capture).toMatchObject({
      type: 'captureMedia',
      tabId: 3,
      url: 'https://cdn.example/master.m3u8',
      kind: 'hls',
      referrer: 'https://page.example/',
      origin: 'https://page.example',
      userAgent: 'TestAgent/1.0',
    });
    expect(capture?.cookies).toHaveLength(1);
    expect(capture?.id).toBe('id-1');
    expect(requested).toHaveLength(0);
  });

  it('clears the tab and its badge when the tab closes', async () => {
    const { detector, store, cleared, badges } = rig();
    await detector.onResponse(details('https://cdn.example/master.m3u8'));
    await detector.onTabRemoved(3);

    expect(await store.list(3)).toEqual([]);
    expect(cleared).toContain(3);
    expect(badges.at(-1)).toEqual({ tabId: 3, count: 1 });
  });
});

describe('media runtime messages', () => {
  it('parses page, list, capture and overlay messages and rejects anything else', () => {
    expect(parseMediaRuntimeMessage({ type: 'mediaPage', pageUrl: 'https://e.example/' })).toEqual({
      type: 'mediaPage',
      pageUrl: 'https://e.example/',
    });
    expect(parseMediaRuntimeMessage({ type: 'mediaList', tabId: 4 })).toEqual({
      type: 'mediaList',
      tabId: 4,
    });
    expect(
      parseMediaRuntimeMessage({ type: 'mediaCapture', tabId: 4, url: 'https://e.example/v.m3u8' }),
    ).toMatchObject({
      type: 'mediaCapture',
    });
    expect(parseMediaRuntimeMessage({ type: 'mediaOverlay' })).toEqual({ type: 'mediaOverlay' });
    expect(parseMediaRuntimeMessage({ type: 'mediaPage' })).toBeUndefined();
    expect(parseMediaRuntimeMessage({ type: 'somethingElse' })).toBeUndefined();
  });
});
