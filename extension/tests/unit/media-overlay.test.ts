import { describe, expect, it } from 'vitest';
import { pageContextOf, pickLargestVideo, type VideoLike } from '../../src/shared/media-overlay';

const video = (width: number, height: number, poster?: string): VideoLike => ({
  ...(poster ? { poster } : {}),
  getBoundingClientRect: () => ({ width, height, left: 10, top: 20 }),
});

describe('media overlay helpers', () => {
  it('picks the largest video on the page', () => {
    expect(
      pickLargestVideo([video(100, 50), video(640, 360), video(320, 180)])?.getBoundingClientRect()
        .width,
    ).toBe(640);
    const largest = video(640, 360);
    expect(pickLargestVideo([video(100, 50), largest, video(320, 180)])).toBe(largest);
    expect(pickLargestVideo([video(0, 0)])).toBeUndefined();
  });

  it('reads the page title and the poster before og:image', () => {
    const page = {
      title: '  A clip  ',
      querySelector: (selector: string) =>
        selector.includes('og:image')
          ? { getAttribute: () => 'https://page.example/og.jpg' }
          : null,
    };
    expect(
      pageContextOf(
        page,
        'https://page.example/watch',
        video(640, 360, 'https://page.example/p.jpg'),
      ),
    ).toEqual({
      type: 'mediaPage',
      pageUrl: 'https://page.example/watch',
      title: 'A clip',
      thumbnailUrl: 'https://page.example/p.jpg',
    });
    expect(pageContextOf(page, 'https://page.example/watch')).toEqual({
      type: 'mediaPage',
      pageUrl: 'https://page.example/watch',
      title: 'A clip',
      thumbnailUrl: 'https://page.example/og.jpg',
    });
  });
});
