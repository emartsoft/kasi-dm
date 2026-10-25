// Classification for media responses seen by webRequest.onResponseStarted (KASI-MED-001, TRD 7.1).
// Pure so the unit tests can drive every URL/content-type/size combination without a browser.

import type { MediaKind } from '../types/protocol';

export const DIRECT_MEDIA_MIN_BYTES = 1_000_000;

const MANIFEST_TYPES: Readonly<Record<string, MediaKind>> = {
  'application/vnd.apple.mpegurl': 'hls',
  'application/x-mpegurl': 'hls',
  'audio/mpegurl': 'hls',
  'audio/x-mpegurl': 'hls',
  'application/dash+xml': 'dash',
};

const SEGMENT_SUFFIXES = ['.ts', '.m4s', '.cmfv', '.cmfa'];

export interface ResponseHeaderLike {
  name?: string;
  value?: string;
}

export interface ResponseDetailsLike {
  url: string;
  tabId: number;
  statusCode?: number;
  type?: string;
  responseHeaders?: ResponseHeaderLike[];
  documentUrl?: string;
  initiator?: string;
  timeStamp?: number;
}

export interface MediaCandidate {
  kind: MediaKind;
  url: string;
  pageUrl: string;
  contentType?: string;
  contentLength?: number;
}

function header(details: ResponseDetailsLike, name: string): string | undefined {
  const wanted = name.toLowerCase();
  const found = details.responseHeaders?.find((item) => item.name?.toLowerCase() === wanted);
  return found?.value;
}

function pathOf(url: string): string {
  try {
    return new URL(url).pathname.toLowerCase();
  } catch {
    return '';
  }
}

function isHttp(url: string): boolean {
  try {
    return ['http:', 'https:'].includes(new URL(url).protocol);
  } catch {
    return false;
  }
}

function mediaType(contentType: string | undefined): string | undefined {
  return contentType?.split(';', 1)[0]?.trim().toLowerCase();
}

function isDirectType(type: string | undefined): boolean {
  return type !== undefined && (type.startsWith('video/') || type.startsWith('audio/'));
}

/**
 * The media kind a response carries, or undefined when it is not something Kasi captures. Segment requests and responses
 * without a 2xx status are ignored; direct media must declare more than one megabyte.
 */
export function classifyResponse(details: ResponseDetailsLike): MediaCandidate | undefined {
  if (details.tabId < 0 || !isHttp(details.url)) return undefined;
  if (details.statusCode !== undefined && (details.statusCode < 200 || details.statusCode >= 300)) {
    return undefined;
  }

  const path = pathOf(details.url);
  if (SEGMENT_SUFFIXES.some((suffix) => path.endsWith(suffix))) return undefined;

  const contentType = mediaType(header(details, 'content-type'));
  const contentLength = Number(header(details, 'content-length'));
  const rangeTotal =
    details.statusCode === 206
      ? Number(/^bytes\s+\d+-\d+\/(\d+)$/i.exec(header(details, 'content-range') ?? '')?.[1])
      : NaN;
  const declaredSize =
    Number.isSafeInteger(rangeTotal) && rangeTotal > 0 ? rangeTotal : contentLength;
  const pageUrl =
    [details.documentUrl, details.initiator].find(
      (value) => value !== undefined && isHttp(value),
    ) ?? details.url;

  if (path.endsWith('.m3u8')) {
    return { kind: 'hls', url: details.url, pageUrl, ...(contentType ? { contentType } : {}) };
  }

  if (path.endsWith('.mpd')) {
    return { kind: 'dash', url: details.url, pageUrl, ...(contentType ? { contentType } : {}) };
  }

  const manifestKind = contentType ? MANIFEST_TYPES[contentType] : undefined;
  if (manifestKind) {
    return {
      kind: manifestKind,
      url: details.url,
      pageUrl,
      ...(contentType ? { contentType } : {}),
    };
  }

  if (
    contentType !== undefined &&
    isDirectType(contentType) &&
    Number.isSafeInteger(declaredSize) &&
    declaredSize > DIRECT_MEDIA_MIN_BYTES
  ) {
    return {
      kind: 'direct',
      url: details.url,
      pageUrl,
      contentType,
      contentLength: declaredSize,
    };
  }

  return undefined;
}
