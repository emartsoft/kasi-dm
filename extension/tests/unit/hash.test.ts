import { describe, expect, it } from 'vitest';
import { createBypass } from '../../src/shared/bypass';
import { hashUrl } from '../../src/shared/hash';
import { createRequestTracker } from '../../src/shared/request-tracker';
import { memoryArea } from './rig';

describe('url hash (real crypto, no mock)', () => {
  it('is a SHA-256 of the URL without its fragment', async () => {
    const hash = await hashUrl('https://example.com/a.zip?token=secret#frag');
    expect(hash).toMatch(/^[0-9a-f]{64}$/);
    expect(hash).toBe(await hashUrl('https://example.com/a.zip?token=secret'));
    expect(hash).not.toBe(await hashUrl('https://example.com/a.zip'));
  });

  it('keeps URLs out of what the tracker and the bypass record write to storage', async () => {
    const area = memoryArea();
    const url = 'https://example.com/a.zip?token=CANARY-QUERY';
    await createRequestTracker(area).onRequest(url, 'POST');
    await createBypass({ area }).record({
      kind: 'kasi-modifier',
      mods: { alt: true, ctrl: false, shift: false },
      href: url,
    });
    expect(JSON.stringify(area.data)).not.toMatch(/example\.com|CANARY/);
  });
});
