// Cookies are secrets (CLAUDE.md rule 3): read only for a download that matched, never logged, held for one hand-off.
import type { Cookie } from '../types/protocol';

export interface CookiesApi {
  getAll(details: { url: string; storeId?: string }): Promise<Cookie[]>;
  getAllCookieStores(): Promise<{ id: string }[]>;
}

const MAX_COOKIES = 200;
const MAX_VALUE = 4096;
const DEFAULT_STORES = ['0', 'firefox-default'];

// A private window has its own store; every other download uses the default one (storeId left out).
export async function storeFor(api: CookiesApi, incognito: boolean): Promise<string | undefined> {
  if (!incognito) return undefined;
  const stores = await api.getAllCookieStores();
  return stores.find((store) => !DEFAULT_STORES.includes(store.id))?.id;
}

// `domain` goes through unchanged: the leading dot of a domain cookie (".example.com" against "example.com" for host-only) is
// the only hostOnly signal the protocol has (plan 2.6).
export async function collectCookies(
  api: CookiesApi,
  urls: string[],
  storeId: string | undefined,
): Promise<Cookie[]> {
  const unique = new Map<string, Cookie>();
  for (const url of new Set(urls)) {
    const found = await api.getAll(storeId === undefined ? { url } : { url, storeId });
    for (const { name, value, domain, path, secure, httpOnly } of found) {
      const valid =
        name.length >= 1 &&
        name.length <= 256 &&
        value.length <= MAX_VALUE &&
        domain.length >= 1 &&
        domain.length <= 255 &&
        path.length >= 1 &&
        path.length <= 2048;
      if (valid) {
        unique.set(`${name}\0${domain}\0${path}`, { name, value, domain, path, secure, httpOnly });
      }
    }
  }
  return [...unique.values()].sort((a, b) => b.path.length - a.path.length).slice(0, MAX_COOKIES);
}
