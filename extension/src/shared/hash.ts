// URLs are never stored: the tracker and the bypass record keep this hash instead (plan 2.4, 2.5).
export async function hashUrl(url: string): Promise<string> {
  let text = url;
  try {
    const parsed = new URL(url);
    parsed.hash = '';
    text = parsed.href;
  } catch {
    // not a URL: hash it as it is
  }
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(text));
  return Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, '0')).join('');
}
