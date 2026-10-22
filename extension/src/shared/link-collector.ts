// Links of a page for "Download all links" and "Download selected links" (KASI-BRW-005, plan m2-5 2.3).
// `collectPageLinks` runs inside the page (scripting.executeScript, every frame) and hands back raw anchors; `normalizeLinks` runs in the
// background and decides what is worth sending. The split keeps the page function tiny and everything with a rule in it a plain unit test.
import type { AddBatch, Cookie } from '../types/protocol';

export type LinkScope = 'all' | 'selection';

export interface RawAnchor {
  /** The resolved address (`anchor.href`). */
  href: string;
  /** The `href` attribute as written, to tell fragment-only links apart. */
  attribute: string;
  /** The `download` attribute, empty when absent. */
  download: string;
}

export interface CollectedLink {
  url: string;
  filename?: string;
}

export const MAX_LINKS = 100_000;
export const MAX_URL_LENGTH = 8192;
// The browser may send the host 64 MiB; a batch is cut well under that so the cookies and the envelope always fit.
export const MAX_BATCH_BYTES = 32 * 1024 * 1024;
const MAX_NAME = 255;
const SCHEMES = ['http:', 'https:', 'ftp:', 'ftps:'];

/**
 * Self-contained on purpose: the browser serialises this function and runs it in the page, so it may use only the page's own globals
 * (never an import or a module-level name). A unit test evaluates its source in an empty scope to keep it that way.
 */
export function collectPageLinks(scope: LinkScope): RawAnchor[] {
  const selection = scope === 'selection' ? window.getSelection() : null;
  if (scope === 'selection' && (!selection || selection.isCollapsed)) return [];
  const found: RawAnchor[] = [];
  for (const anchor of Array.from(document.querySelectorAll('a[href]'))) {
    const href: unknown = (anchor as { href?: unknown }).href;
    if (typeof href !== 'string') continue; // an SVG anchor
    if (selection && !selection.containsNode(anchor, true)) continue;
    found.push({
      href,
      attribute: anchor.getAttribute('href') ?? '',
      download: anchor.getAttribute('download') ?? '',
    });
  }
  return found;
}

function cut(name: string): string {
  if (name.length <= MAX_NAME) return name;
  const dot = name.lastIndexOf('.');
  const extension = dot > 0 && name.length - dot <= 32 ? name.slice(dot) : '';
  return name.slice(0, MAX_NAME - extension.length) + extension;
}

// The `download` attribute names the file when it is there, else the last path segment does; no hint when neither says anything.
function filenameFor(anchor: RawAnchor, url: URL): string | undefined {
  const named = anchor.download.split(/[\\/]/).pop()?.trim();
  if (named) return cut(named);
  try {
    const segment = decodeURIComponent(url.pathname.split('/').pop() ?? '').trim();
    return segment ? cut(segment) : undefined;
  } catch {
    return undefined;
  }
}

/** What executeScript returned for one frame, checked before use: only well-formed anchors pass. */
export function parseRawAnchors(value: unknown): RawAnchor[] {
  if (!Array.isArray(value)) return [];
  const anchors: RawAnchor[] = [];
  for (const entry of value as unknown[]) {
    if (typeof entry !== 'object' || entry === null) continue;
    const { href, attribute, download } = entry as Record<string, unknown>;
    if (typeof href === 'string' && typeof attribute === 'string' && typeof download === 'string') {
      anchors.push({ href, attribute, download });
    }
  }
  return anchors;
}

export interface Normalized {
  links: CollectedLink[];
  /** True when more than MAX_LINKS were found and the rest was dropped. */
  truncated: boolean;
}

/** http, https and ftp only, at most 8,192 characters, no fragment-only links, no duplicates (the first one wins), at most MAX_LINKS. */
export function normalizeLinks(anchors: readonly RawAnchor[]): Normalized {
  const seen = new Set<string>();
  const links: CollectedLink[] = [];
  let truncated = false;
  for (const anchor of anchors) {
    if (anchor.attribute.trim().startsWith('#')) continue;
    if (anchor.href.length > MAX_URL_LENGTH) continue;
    let url: URL;
    try {
      url = new URL(anchor.href);
    } catch {
      continue;
    }
    if (!SCHEMES.includes(url.protocol)) continue;
    const key = url.href;
    if (seen.has(key)) continue;
    if (links.length === MAX_LINKS) {
      truncated = true;
      break;
    }
    seen.add(key);
    const filename = filenameFor(anchor, url);
    links.push(filename === undefined ? { url: key } : { url: key, filename });
  }
  return { links, truncated };
}

export function isDownloadableUrl(value: string | undefined): value is string {
  if (!value || value.length > MAX_URL_LENGTH) return false;
  try {
    return SCHEMES.includes(new URL(value).protocol);
  } catch {
    return false;
  }
}

function isWebUrl(value: string | undefined): value is string {
  if (!value || value.length > MAX_URL_LENGTH) return false;
  try {
    const { protocol } = new URL(value);
    return protocol === 'http:' || protocol === 'https:';
  } catch {
    return false;
  }
}

/** One representative URL per distinct origin (at most `limit`), so a cookie lookup covers every host in a batch once. */
export function originsOf(links: readonly CollectedLink[], limit: number): string[] {
  const byOrigin = new Map<string, string>();
  for (const { url } of links) {
    if (byOrigin.size >= limit) break;
    const origin = new URL(url).origin;
    if (!byOrigin.has(origin)) byOrigin.set(origin, url);
  }
  return [...byOrigin.values()];
}

/**
 * The `addBatch` message, or undefined when it cannot be one (no links, or a page that is not http or https: the schema needs a web
 * `referrer`). Links are kept in order and the tail is dropped when the serialised message would pass MAX_BATCH_BYTES.
 */
export function buildAddBatch(
  id: string,
  referrer: string | undefined,
  links: readonly CollectedLink[],
  userAgent: string,
  cookies: Cookie[],
): AddBatch | undefined {
  if (!isWebUrl(referrer) || links.length === 0) return undefined;
  const encoder = new TextEncoder();
  const bytesOf = (value: unknown): number => encoder.encode(JSON.stringify(value)).length;
  let bytes = bytesOf({
    v: 1,
    id,
    type: 'addBatch',
    referrer,
    links: [],
    cookies,
    userAgent: userAgent.slice(0, 1024),
  });
  const kept: CollectedLink[] = [];
  for (const link of links.slice(0, MAX_LINKS)) {
    const size = bytesOf(link) + 1; // plus the comma
    if (bytes + size > MAX_BATCH_BYTES) break;
    bytes += size;
    kept.push(link);
  }
  const [first, ...rest] = kept;
  if (first === undefined) return undefined;
  return {
    v: 1,
    id,
    type: 'addBatch',
    referrer,
    links: [first, ...rest],
    cookies,
    userAgent: userAgent.slice(0, 1024),
  };
}
