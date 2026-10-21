// Pure rule semantics (TRD 9.2, docs/protocol/README.md "Rule semantics"). No browser API is touched here, so every branch is a unit test.
import type { Rules } from '../types/protocol';

export interface DownloadFacts {
  url: string;
  finalUrl?: string;
  referrer?: string;
  filename?: string;
  mime?: string;
  // -1 or absent: the browser does not know the size yet.
  totalBytes?: number | undefined;
  incognito?: boolean;
  byExtensionId?: string;
}

export interface ModifierState {
  alt: boolean;
  ctrl: boolean;
  shift: boolean;
}

const SCHEMES = ['http:', 'https:', 'ftp:'];
const MAX_URL_LENGTH = 8192;

function parse(url: string | undefined): URL | undefined {
  if (!url) return undefined;
  try {
    return new URL(url);
  } catch {
    return undefined;
  }
}

function lastSegment(url: URL): string {
  const segment = url.pathname.split('/').pop() ?? '';
  try {
    return decodeURIComponent(segment);
  } catch {
    return segment;
  }
}

// The browser's file name when it has one, else the last path segment of the final URL, else of the URL.
function fileName(facts: DownloadFacts): string {
  const named = facts.filename?.split(/[\\/]/).pop();
  if (named) return named.toLowerCase();
  const url = parse(facts.finalUrl) ?? parse(facts.url);
  return url ? lastSegment(url).toLowerCase() : '';
}

function normalise(entry: string): string {
  return entry.trim().toLowerCase().replace(/^\.+/, '');
}

function extensionMatches(rules: Rules, facts: DownloadFacts): boolean {
  const name = fileName(facts);
  return rules.extensions.some((entry) => {
    const ext = normalise(entry);
    return ext !== '' && name.endsWith(`.${ext}`);
  });
}

function mimeMatches(rules: Rules, facts: DownloadFacts): boolean {
  const mime = (facts.mime ?? '').split(';')[0]?.trim().toLowerCase() ?? '';
  if (mime === '') return false;
  return rules.mimeTypes.some((entry) => {
    const rule = entry.trim().toLowerCase();
    return rule.endsWith('/*')
      ? mime.startsWith(rule.slice(0, -1)) && mime.length > rule.length - 1
      : mime === rule;
  });
}

// An entry excludes that host and its subdomains; it is tested against the URL, the final URL and the referrer.
export function isExcludedSite(rules: Rules, facts: DownloadFacts): boolean {
  const hosts = [facts.url, facts.finalUrl, facts.referrer]
    .map((value) => parse(value)?.hostname.toLowerCase().replace(/\.$/, ''))
    .filter((host): host is string => !!host);
  return rules.excludedSites.some((entry) => {
    const site = normalise(entry).replace(/^\*\./, '');
    return site !== '' && hosts.some((host) => host === site || host.endsWith(`.${site}`));
  });
}

// What is never intercepted whatever the rules say (TRD 9.2, TC-BRW-004). The POST-form check needs request history and lives with the tracker.
export function isEligible(facts: DownloadFacts, incognitoAllowed: boolean): boolean {
  if (facts.byExtensionId) return false;
  if (facts.incognito && !incognitoAllowed) return false;
  return [facts.url, facts.finalUrl ?? facts.url].every(
    (value) => value.length <= MAX_URL_LENGTH && SCHEMES.includes(parse(value)?.protocol ?? ''),
  );
}

// Empty lists match nothing: never take a download that was not asked for. An unknown size is intercepted; Kasi's dialog lets the user decline.
export function matchesRules(rules: Rules, facts: DownloadFacts): boolean {
  if (!rules.enabled || isExcludedSite(rules, facts)) return false;
  const size = facts.totalBytes ?? -1;
  if (rules.minSizeBytes !== undefined && size >= 0 && size < rules.minSizeBytes) return false;
  return extensionMatches(rules, facts) || mimeMatches(rules, facts);
}

// `none` turns the bypass off; otherwise only the configured key counts, whatever else is held.
export function bypassHeld(bypassKey: Rules['bypassKey'], held: ModifierState): boolean {
  return bypassKey !== 'none' && held[bypassKey];
}
