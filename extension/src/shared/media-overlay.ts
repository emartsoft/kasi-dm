// The in-page overlay: a "Save with Kasi" button on the largest <video> and the page title/poster sent to the worker
// (KASI-MED-001). Only what the page already displays is read; there is no hidden-URL scraping.

export interface VideoLike {
  currentSrc?: string;
  src?: string;
  poster?: string;
  getBoundingClientRect(): { width: number; height: number; left: number; top: number };
}

export interface PageContextMessage {
  type: 'mediaPage';
  pageUrl: string;
  title?: string;
  thumbnailUrl?: string;
}

export interface OverlayMessage {
  type: 'mediaOverlay';
}

export function pickLargestVideo(videos: Iterable<VideoLike>): VideoLike | undefined {
  let best: VideoLike | undefined;
  let bestArea = 0;
  for (const video of videos) {
    const rect = video.getBoundingClientRect();
    const area = Math.max(0, rect.width) * Math.max(0, rect.height);
    if (area > bestArea) {
      best = video;
      bestArea = area;
    }
  }

  return best;
}

export function pageContextOf(
  documentLike: {
    title: string;
    querySelector(selector: string): { getAttribute(name: string): string | null } | null;
  },
  pageUrl: string,
  video?: VideoLike,
): PageContextMessage {
  const image = documentLike.querySelector('meta[property="og:image"]')?.getAttribute('content');
  const thumbnailUrl =
    video?.poster && video.poster.length > 0 ? video.poster : (image ?? undefined);
  const title = documentLike.title.trim();
  return {
    type: 'mediaPage',
    pageUrl,
    ...(title ? { title } : {}),
    ...(thumbnailUrl ? { thumbnailUrl } : {}),
  };
}

export interface OverlayRoot {
  body: {
    appendChild(child: unknown): unknown;
  };
  createElement(tag: string): {
    textContent: string | null;
    title: string;
    className: string;
    style: Record<string, string>;
    setAttribute(name: string, value: string): void;
    addEventListener(type: string, listener: () => void): void;
  };
  querySelectorAll(selector: string): Iterable<VideoLike>;
  addEventListener(type: string, listener: () => void, options?: { once?: boolean }): void;
}

export function installMediaOverlay(
  root: OverlayRoot,
  send: (message: OverlayMessage) => void,
  label: string,
  pageUrl: string,
  documentLike: Parameters<typeof pageContextOf>[0],
  reportContext: (context: PageContextMessage) => void = () => {
    // no context sink
  },
): () => void {
  const button = root.createElement('button');
  button.textContent = label;
  button.title = label;
  button.className = 'kasi-media-overlay';
  button.setAttribute('aria-label', label);
  button.style.position = 'fixed';
  button.style.zIndex = '2147483647';
  button.style.display = 'none';
  button.style.padding = '6px 10px';
  button.style.borderRadius = '4px';
  button.style.border = '1px solid rgba(255,255,255,.5)';
  button.style.background = '#0f6cbd';
  button.style.color = '#fff';
  button.style.font = '13px system-ui, sans-serif';
  button.style.cursor = 'pointer';
  root.body.appendChild(button);
  button.addEventListener('click', () => {
    send({ type: 'mediaOverlay' });
  });

  let lastContext = '';
  const update = (): void => {
    const video = pickLargestVideo(root.querySelectorAll('video'));
    if (!video) {
      button.style.display = 'none';
      return;
    }

    const rect = video.getBoundingClientRect();
    if (rect.width < 120 || rect.height < 80) {
      button.style.display = 'none';
      return;
    }

    button.style.display = 'block';
    button.style.left = `${String(Math.max(8, rect.left + rect.width - 150))}px`;
    button.style.top = `${String(Math.max(8, rect.top + 8))}px`;

    const context = pageContextOf(documentLike, pageUrl, video);
    const serialized = JSON.stringify(context);
    if (serialized !== lastContext) {
      lastContext = serialized;
      reportContext(context);
    }
  };

  const onScroll = (): void => {
    update();
  };
  root.addEventListener('scroll', onScroll);
  const timer = setInterval(update, 500);
  update();
  return () => {
    clearInterval(timer);
    button.style.display = 'none';
  };
}
