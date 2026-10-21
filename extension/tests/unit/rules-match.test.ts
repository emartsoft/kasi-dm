import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { parseRules } from '../../src/shared/messages';
import {
  bypassHeld,
  isEligible,
  isExcludedSite,
  matchesRules,
  type DownloadFacts,
} from '../../src/shared/rules-match';
import type { Rules } from '../../src/types/protocol';

const base: Rules = {
  v: 1,
  id: 'r',
  type: 'rules',
  enabled: true,
  extensions: ['zip', 'tar.gz'],
  mimeTypes: ['application/zip', 'video/*'],
  excludedSites: ['bank.example.com'],
  bypassKey: 'alt',
  ackTimeoutMs: 2000,
};
const rules = (over: Partial<Rules> = {}): Rules => ({ ...base, ...over });
const facts = (over: Partial<DownloadFacts> = {}): DownloadFacts => ({
  url: 'https://files.example.org/a.zip',
  filename: 'a.zip',
  ...over,
});

describe('[TC-BRW-001] extension and MIME matching', () => {
  it.each([
    ['a.zip', true],
    ['A.ZIP', true],
    ['a.tar.gz', true],
    ['a.gz', false],
    ['zip', false],
    ['a.zipx', false],
    ['C:\\Users\\me\\Downloads\\a.zip', true],
    ['', false],
  ])('file name %s -> %s', (filename, expected) => {
    const f = facts({ url: 'https://x.example.org/', filename });
    expect(matchesRules(rules(), f)).toBe(expected);
  });

  it('falls back to the final URL path, percent-decoded, when the browser has no name yet', () => {
    const f = {
      url: 'https://x.example.org/get',
      finalUrl: 'https://cdn.example.org/My%20File.ZIP',
    };
    expect(matchesRules(rules(), f)).toBe(true);
    expect(matchesRules(rules(), { url: 'https://x.example.org/a.zip?name=b.txt' })).toBe(true);
    expect(matchesRules(rules(), { url: 'https://x.example.org/%E0%A4%A.txt' })).toBe(false);
  });

  it('tolerates a leading dot and upper case in an entry', () => {
    expect(matchesRules(rules({ extensions: ['.ZIP'] }), facts())).toBe(true);
  });

  it.each([
    ['application/zip', true],
    ['Application/ZIP; charset=binary', true],
    ['video/mp4', true],
    ['video/', false],
    ['application/x-zip', false],
    ['audio/mp3', false],
    ['', false],
  ])('MIME %s -> %s', (mime, expected) => {
    const f = facts({ filename: 'a.bin', url: 'https://x.example.org/a.bin', mime });
    expect(matchesRules(rules(), f)).toBe(expected);
  });

  it('matches on either the extension or the MIME type', () => {
    expect(matchesRules(rules(), facts({ filename: 'a.bin', mime: 'application/zip' }))).toBe(true);
    expect(matchesRules(rules(), facts({ filename: 'a.zip', mime: 'text/html' }))).toBe(true);
  });

  it('matches nothing with empty lists, and nothing when disabled', () => {
    const empty = rules({ extensions: [], mimeTypes: [] });
    expect(matchesRules(empty, facts({ mime: 'video/mp4' }))).toBe(false);
    expect(matchesRules(rules({ enabled: false }), facts())).toBe(false);
  });
});

describe('[TC-BRW-004] excluded sites', () => {
  it.each([
    ['https://bank.example.com/a.zip', true],
    ['https://www.bank.example.com/a.zip', true],
    ['https://BANK.example.com./a.zip', true],
    ['https://notbank.example.com/a.zip', false],
    ['https://example.com/a.zip', false],
  ])('%s -> excluded %s', (url, excluded) => {
    expect(isExcludedSite(rules(), facts({ url }))).toBe(excluded);
  });

  it('tests the final URL and the referrer too, any hit excludes', () => {
    const f = facts({ finalUrl: 'https://cdn.example.org/a.zip' });
    expect(matchesRules(rules(), { ...f, finalUrl: 'https://bank.example.com/a.zip' })).toBe(false);
    expect(matchesRules(rules(), { ...f, referrer: 'https://bank.example.com/page' })).toBe(false);
    expect(matchesRules(rules(), { ...f, referrer: 'https://news.example.org/' })).toBe(true);
  });

  it('accepts a wildcard or dotted entry and ignores an empty one', () => {
    const url = 'https://a.bank.example.com/a.zip';
    expect(isExcludedSite(rules({ excludedSites: ['*.bank.example.com'] }), facts({ url }))).toBe(
      true,
    );
    expect(isExcludedSite(rules({ excludedSites: ['.bank.example.com'] }), facts({ url }))).toBe(
      true,
    );
    expect(isExcludedSite(rules({ excludedSites: ['', ' '] }), facts({ url }))).toBe(false);
  });
});

describe('minimum size', () => {
  const min = rules({ minSizeBytes: 1000 });

  it.each([
    [999, false],
    [1000, true],
    [5_000_000, true],
    [0, false],
    [-1, true],
    [undefined, true],
  ])('totalBytes %s -> intercepted %s', (totalBytes, expected) => {
    expect(matchesRules(min, facts({ totalBytes }))).toBe(expected);
  });

  it('has no minimum without the field, or with 0', () => {
    expect(matchesRules(rules(), facts({ totalBytes: 1 }))).toBe(true);
    expect(matchesRules(rules({ minSizeBytes: 0 }), facts({ totalBytes: 0 }))).toBe(true);
  });

  it('is read by the rules parser, and bad values are refused', () => {
    const read = (name: string) =>
      parseRules(
        JSON.parse(
          readFileSync(new URL(`../../../tests/fixtures/ipc/${name}`, import.meta.url), 'utf8'),
        ),
      );
    expect(read('valid/rules-min-size.json')?.minSizeBytes).toBe(1048576);
    expect(read('valid/rules.json')?.minSizeBytes).toBeUndefined();
    expect(read('invalid/rules--min-size-negative.json')).toBeUndefined();
    expect(read('invalid/rules--min-size-fraction.json')).toBeUndefined();
    expect(read('invalid/rules--min-size-above-cap.json')).toBeUndefined();
  });
});

describe('[TC-BRW-004] never intercepted', () => {
  it.each([
    ['https://a.example.org/a.zip', true],
    ['http://a.example.org/a.zip', true],
    ['ftp://a.example.org/a.zip', true],
    ['blob:https://a.example.org/6f1c', false],
    ['data:application/zip;base64,AAAA', false],
    ['file:///C:/a.zip', false],
    ['filesystem:https://a.example.org/temporary/a.zip', false],
    ['about:blank', false],
    ['not a url', false],
    [`https://a.example.org/${'x'.repeat(8192)}`, false],
  ])('scheme and length: %s -> %s', (url, expected) => {
    expect(isEligible(facts({ url }), false)).toBe(expected);
  });

  it('refuses when the final URL is blob:', () => {
    expect(isEligible(facts({ finalUrl: 'blob:https://a.example.org/6f1c' }), false)).toBe(false);
    expect(isEligible(facts({ finalUrl: 'https://cdn.example.org/a.zip' }), false)).toBe(true);
  });

  it('refuses another extension download, ours included', () => {
    expect(isEligible(facts({ byExtensionId: 'abc' }), true)).toBe(false);
  });

  it('refuses incognito unless the user allowed the extension there', () => {
    expect(isEligible(facts({ incognito: true }), false)).toBe(false);
    expect(isEligible(facts({ incognito: true }), true)).toBe(true);
    expect(isEligible(facts({ incognito: false }), false)).toBe(true);
  });
});

describe('[TC-BRW-003] bypass key value', () => {
  const held = (alt = false, ctrl = false, shift = false) => ({ alt, ctrl, shift });

  it.each([
    ['alt', held(true), true],
    ['alt', held(false, true), false],
    ['ctrl', held(false, true), true],
    ['shift', held(false, false, true), true],
    ['shift', held(true), false],
    ['alt', held(true, true, true), true],
    ['alt', held(), false],
  ] as const)('%s with %j -> %s', (key, state, expected) => {
    expect(bypassHeld(key, state)).toBe(expected);
  });

  it('is off for none, whatever is held', () => {
    expect(bypassHeld('none', held(true, true, true))).toBe(false);
  });
});
