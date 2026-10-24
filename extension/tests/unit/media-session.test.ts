import { describe, expect, it } from 'vitest';
import {
  createMediaSession,
  MAX_DETECTIONS_PER_TAB,
  MEDIA_STORAGE_KEY,
  type MediaDetection,
  type SessionArea,
} from '../../src/shared/media-session';

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

const detection = (tabId: number, url: string, at = 1): MediaDetection => ({
  tabId,
  pageUrl: 'https://page.example/',
  kind: 'hls',
  url,
  detectedAt: at,
});

describe('media session store', () => {
  it('keeps a detection across a worker restart, deduplicates by URL and enriches it with the page context', async () => {
    const area = new FakeArea();
    const first = createMediaSession(area);
    await first.setContext(7, {
      pageUrl: 'https://page.example/',
      title: 'Clip',
      thumbnailUrl: 'https://page.example/p.jpg',
    });
    await first.add(detection(7, 'https://cdn.example/master.m3u8'));
    await first.add(detection(7, 'https://cdn.example/master.m3u8', 2));

    const restarted = createMediaSession(area);
    const list = await restarted.list(7);
    expect(list).toHaveLength(1);
    expect(list[0]?.title).toBe('Clip');
    expect(list[0]?.thumbnailUrl).toBe('https://page.example/p.jpg');
    expect(area.data[MEDIA_STORAGE_KEY]).toBeDefined();
  });

  it('bounds one tab and the number of tabs', async () => {
    const area = new FakeArea();
    const store = createMediaSession(area);
    for (let index = 0; index < MAX_DETECTIONS_PER_TAB + 5; index++) {
      await store.add(detection(1, `https://cdn.example/${String(index)}.m3u8`, index));
    }
    expect(await store.list(1)).toHaveLength(MAX_DETECTIONS_PER_TAB);
    expect((await store.list(1))[0]?.url).toBe('https://cdn.example/5.m3u8');

    for (let tab = 2; tab <= 260; tab++)
      await store.add(detection(tab, 'https://cdn.example/x.m3u8', tab));
    expect(new Set((await store.all()).map((item) => item.tabId)).size).toBeLessThanOrEqual(256);
    await store.removeTab(260);
    expect(await store.list(260)).toEqual([]);
    await store.clear();
    expect(await store.all()).toEqual([]);
  });
});
