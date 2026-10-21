// Stand-in for src/shared/hash.ts in suites that run under fake timers: real crypto.subtle resolves off the microtask queue.
export function hashUrl(url: string): Promise<string> {
  try {
    const parsed = new URL(url);
    parsed.hash = '';
    return Promise.resolve(`h:${parsed.href}`);
  } catch {
    return Promise.resolve(`h:${url}`);
  }
}
