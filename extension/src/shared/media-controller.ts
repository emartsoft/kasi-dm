// The media side of the background worker: classify responses, keep the per-tab session list, paint the badge, answer the
// popup/content script and turn a chosen stream into a captureMedia message for Kasi (KASI-MED-001).

import type { CaptureMedia, MediaDetected, MediaKind } from '../types/protocol';
import { classifyResponse, type ResponseDetailsLike } from './media-detect';
import type { MediaDetection, PageContext } from './media-session';

export interface MediaBadge {
  set(tabId: number, count: number): void;
  clear(tabId: number): void;
}

export interface MediaTabs {
  sendMessage(tabId: number, message: unknown): Promise<void>;
}

export interface MediaCaptureRequest {
  request(message: unknown): Promise<unknown>;
}

export interface MediaLogger {
  info(message: string, data?: Record<string, unknown>): void;
  error(message: string, data?: Record<string, unknown>): void;
}

export interface MediaDetectorDeps {
  store: {
    add(detection: MediaDetection): Promise<boolean>;
    list(tabId: number): Promise<MediaDetection[]>;
    context(tabId: number): Promise<PageContext | undefined>;
    setContext(tabId: number, context: PageContext): Promise<void>;
    removeTab(tabId: number): Promise<void>;
  };
  badge: MediaBadge;
  tabs: MediaTabs;
  port: MediaCaptureRequest;
  cookiesFor(url: string, tabId: number): Promise<CaptureMedia['cookies']>;
  pageUrlForTab?(tabId: number): Promise<string | undefined>;
  userAgent: string;
  onDetected?(detection: MediaDetection): Promise<void>;
  now(): number;
  newId(): string;
  logger: MediaLogger;
}

export interface MediaDetector {
  onResponse(details: ResponseDetailsLike): Promise<MediaDetection | undefined>;
  onPageContext(tabId: number, context: PageContext): Promise<void>;
  onTabRemoved(tabId: number): Promise<void>;
  onPageNavigation(tabId: number): Promise<void>;
  list(tabId: number): Promise<MediaDetection[]>;
  capture(tabId: number, url?: string): Promise<CaptureMedia | undefined>;
}

export function createMediaDetector(deps: MediaDetectorDeps): MediaDetector {
  async function clearTab(tabId: number): Promise<void> {
    await deps.store.removeTab(tabId);
    deps.badge.clear(tabId);
  }

  async function refreshBadge(tabId: number): Promise<void> {
    const list = await deps.store.list(tabId);
    if (list.length === 0) deps.badge.clear(tabId);
    else deps.badge.set(tabId, list.length);
  }

  async function notify(tabId: number): Promise<void> {
    try {
      await deps.tabs.sendMessage(tabId, { type: 'mediaChanged' });
    } catch {
      // no content script in that tab (a browser page, PDF viewer, or a frame that navigated)
    }
  }

  return {
    async onResponse(details) {
      const candidate = classifyResponse(details);
      if (!candidate) return undefined;
      const context = await deps.store.context(details.tabId);
      let tabPageUrl: string | undefined;
      try {
        tabPageUrl = await deps.pageUrlForTab?.(details.tabId);
      } catch {
        // The tab may have closed between the response event and this lookup.
      }
      const matchingTabUrl =
        tabPageUrl &&
        (candidate.pageUrl === candidate.url || sameOrigin(tabPageUrl, candidate.pageUrl))
          ? tabPageUrl
          : undefined;
      const detection: MediaDetection = {
        tabId: details.tabId,
        pageUrl: context?.pageUrl ?? matchingTabUrl ?? candidate.pageUrl,
        kind: candidate.kind,
        url: candidate.url,
        detectedAt: deps.now(),
        ...(context?.title ? { title: context.title } : {}),
        ...(context?.thumbnailUrl ? { thumbnailUrl: context.thumbnailUrl } : {}),
        ...(candidate.contentType ? { contentType: candidate.contentType } : {}),
      };
      const added = await deps.store.add(detection);
      if (added) {
        await refreshBadge(details.tabId);
        await notify(details.tabId);
        try {
          await deps.onDetected?.(detection);
        } catch {
          deps.logger.info('Media detection could not reach Kasi', { kind: candidate.kind });
        }
        deps.logger.info('Media detected', { kind: candidate.kind, tabId: details.tabId });
      }

      return detection;
    },

    async onPageContext(tabId, context) {
      if (tabId < 0) return;
      await deps.store.setContext(tabId, context);
      await refreshBadge(tabId);
      await notify(tabId);
    },

    async onTabRemoved(tabId) {
      await clearTab(tabId);
    },

    onPageNavigation: clearTab,

    list: (tabId) => deps.store.list(tabId),

    async capture(tabId, url) {
      const list = await deps.store.list(tabId);
      const chosen = url === undefined ? list.at(-1) : list.find((item) => item.url === url);
      if (!chosen) return undefined;
      const context = await deps.store.context(tabId);
      const origin = (() => {
        try {
          return new URL(context?.pageUrl ?? chosen.pageUrl).origin;
        } catch {
          return undefined;
        }
      })();
      return {
        v: 1,
        id: deps.newId(),
        type: 'captureMedia',
        tabId,
        url: chosen.url,
        kind: chosen.kind,
        cookies: await deps.cookiesFor(chosen.url, tabId),
        referrer: context?.pageUrl ?? chosen.pageUrl,
        userAgent: deps.userAgent,
        ...(origin ? { origin } : {}),
      };
    },
  };
}

function sameOrigin(left: string, right: string): boolean {
  try {
    return new URL(left).origin === new URL(right).origin;
  } catch {
    return false;
  }
}

function webUrl(value: string, base?: string): string | undefined {
  try {
    const url = new URL(value, base);
    return ['http:', 'https:'].includes(url.protocol) && url.href.length <= 8192
      ? url.href
      : undefined;
  } catch {
    return undefined;
  }
}

export function createMediaDetectedMessage(
  detection: MediaDetection,
  id: string,
): MediaDetected | undefined {
  const pageUrl = webUrl(detection.pageUrl);
  const url = webUrl(detection.url);
  if (
    !pageUrl ||
    !url ||
    id.length < 1 ||
    id.length > 64 ||
    !Number.isInteger(detection.tabId) ||
    detection.tabId < 0
  ) {
    return undefined;
  }
  const thumbnailUrl = detection.thumbnailUrl ? webUrl(detection.thumbnailUrl, pageUrl) : undefined;
  return {
    v: 1,
    id,
    type: 'mediaDetected',
    tabId: detection.tabId,
    pageUrl,
    kind: detection.kind,
    url,
    ...(detection.title ? { title: detection.title.slice(0, 1024) } : {}),
    ...(thumbnailUrl ? { thumbnailUrl } : {}),
    ...(detection.contentType ? { contentType: detection.contentType.slice(0, 255) } : {}),
  };
}

export type MediaRuntimeMessage =
  | { type: 'mediaPage'; title?: string; thumbnailUrl?: string; pageUrl: string }
  | { type: 'mediaList'; tabId?: number }
  | { type: 'mediaCapture'; tabId?: number; url?: string }
  | { type: 'mediaOverlay' }
  | { type: 'mediaChanged' };

export function parseMediaRuntimeMessage(value: unknown): MediaRuntimeMessage | undefined {
  if (typeof value !== 'object' || value === null || !('type' in value)) return undefined;
  const message = value as Record<string, unknown>;
  switch (message.type) {
    case 'mediaPage':
      return typeof message.pageUrl === 'string'
        ? {
            type: 'mediaPage',
            pageUrl: message.pageUrl,
            ...(typeof message.title === 'string' ? { title: message.title } : {}),
            ...(typeof message.thumbnailUrl === 'string'
              ? { thumbnailUrl: message.thumbnailUrl }
              : {}),
          }
        : undefined;
    case 'mediaList':
      return {
        type: 'mediaList',
        ...(typeof message.tabId === 'number' ? { tabId: message.tabId } : {}),
      };
    case 'mediaCapture':
      return {
        type: 'mediaCapture',
        ...(typeof message.tabId === 'number' ? { tabId: message.tabId } : {}),
        ...(typeof message.url === 'string' ? { url: message.url } : {}),
      };
    case 'mediaOverlay':
      return { type: 'mediaOverlay' };
    case 'mediaChanged':
      return { type: 'mediaChanged' };
    default:
      return undefined;
  }
}

export function kindLabel(kind: MediaKind): string {
  return kind === 'hls' ? 'HLS' : kind === 'dash' ? 'DASH' : 'Media';
}
