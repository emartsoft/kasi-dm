import { describe, expect, it } from 'vitest';
import {
  classifyResponse,
  DIRECT_MEDIA_MIN_BYTES,
  type ResponseDetailsLike,
} from '../../src/shared/media-detect';

const response = (overrides: Partial<Parameters<typeof classifyResponse>[0]> = {}) => ({
  tabId: 7,
  url: 'https://cdn.example/video.m3u8',
  statusCode: 200,
  documentUrl: 'https://page.example/watch',
  ...overrides,
});

describe('classifyResponse', () => {
  it.each([
    ['https://cdn.example/master.m3u8', 'hls'],
    ['https://cdn.example/manifest.mpd?token=x', 'dash'],
    ['https://cdn.example/play?format=hls', 'hls'],
  ] as const)('classifies %s as %s by URL or content type', (url, kind) => {
    const details =
      kind === 'hls' && url.includes('format=hls')
        ? response({
            url,
            responseHeaders: [
              { name: 'content-type', value: 'application/vnd.apple.mpegurl; charset=utf-8' },
            ],
          })
        : response({ url });
    expect(classifyResponse(details)?.kind).toBe(kind);
  });

  it('classifies DASH by its media type', () => {
    expect(
      classifyResponse(
        response({
          url: 'https://cdn.example/stream',
          responseHeaders: [{ name: 'Content-Type', value: 'application/dash+xml' }],
        }),
      )?.kind,
    ).toBe('dash');
  });

  it('accepts direct media only when it declares more than one megabyte', () => {
    const details = (length: string) =>
      response({
        url: 'https://cdn.example/movie',
        responseHeaders: [
          { name: 'content-type', value: 'video/mp4' },
          { name: 'content-length', value: length },
        ],
      });
    expect(classifyResponse(details(String(DIRECT_MEDIA_MIN_BYTES + 1)))?.kind).toBe('direct');
    expect(classifyResponse(details(String(DIRECT_MEDIA_MIN_BYTES)))).toBeUndefined();
    expect(classifyResponse(details('unknown'))).toBeUndefined();
  });

  it.each(['.ts', '.m4s', '.cmfv', '.cmfa'])(
    'suppresses segment %s even with a video type',
    (suffix) => {
      expect(
        classifyResponse(
          response({
            url: `https://cdn.example/segment${suffix}`,
            responseHeaders: [
              { name: 'content-type', value: 'video/mp4' },
              { name: 'content-length', value: '9000000' },
            ],
          }),
        ),
      ).toBeUndefined();
    },
  );

  it('ignores failed, non-web and background requests', () => {
    expect(classifyResponse(response({ statusCode: 404 }))).toBeUndefined();
    expect(classifyResponse(response({ url: 'blob:https://page.example/id' }))).toBeUndefined();
    expect(classifyResponse(response({ tabId: -1 }))).toBeUndefined();
  });

  it('uses the document URL as the page URL and falls back to the manifest', () => {
    expect(classifyResponse(response())?.pageUrl).toBe('https://page.example/watch');
    const withoutDocument: ResponseDetailsLike = { ...response() };
    delete withoutDocument.documentUrl;
    delete withoutDocument.initiator;
    expect(classifyResponse(withoutDocument)?.pageUrl).toBe('https://cdn.example/video.m3u8');
  });
});
