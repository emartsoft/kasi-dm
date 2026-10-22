// KASI-BRW-005, plan m2-5 2.3: which links of a page go to Kasi, and the addBatch they travel in. No DOM library: the page function
// is tested against hand-made globals, the rest is plain data in, plain data out.
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { Ajv } from 'ajv';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  buildAddBatch,
  collectPageLinks,
  isDownloadableUrl,
  MAX_BATCH_BYTES,
  MAX_LINKS,
  normalizeLinks,
  originsOf,
  parseRawAnchors,
  type RawAnchor,
} from '../../src/shared/link-collector';

const schema = JSON.parse(
  readFileSync(
    new URL('../../../docs/protocol/kasi-messages.schema.json', import.meta.url),
    'utf8',
  ),
) as object;
const validate = new Ajv({ allErrors: true, strict: true }).compile(schema);

const anchor = (href: string, over: Partial<RawAnchor> = {}): RawAnchor => ({
  href,
  attribute: href,
  download: '',
  ...over,
});

afterEach(() => {
  vi.unstubAllGlobals();
});

interface FakeAnchor {
  href: unknown;
  getAttribute(name: string): string | null;
}

function fakeAnchor(href: unknown, attributes: Record<string, string> = {}): FakeAnchor {
  return { href, getAttribute: (name) => attributes[name] ?? null };
}

function stubPage(anchors: FakeAnchor[], selection?: { collapsed: boolean; inside: FakeAnchor[] }) {
  vi.stubGlobal('document', { querySelectorAll: () => anchors });
  vi.stubGlobal('window', {
    getSelection: () =>
      selection
        ? {
            isCollapsed: selection.collapsed,
            containsNode: (node: FakeAnchor) => selection.inside.includes(node),
          }
        : null,
  });
}

describe('[TC-BRW-005] the page function', () => {
  it('runs from its own source alone, because the browser serialises it into the page', () => {
    const a = fakeAnchor('https://example.com/a.zip', { href: 'a.zip', download: 'mine.zip' });
    // a new context holds nothing but the page's globals: an import or a module-level name in the function would throw here
    const page = {
      document: { querySelectorAll: () => [a] },
      window: { getSelection: () => null },
    };
    const standalone = runInNewContext(
      `(${collectPageLinks.toString()})`,
      page,
    ) as typeof collectPageLinks;

    expect(JSON.parse(JSON.stringify(standalone('all')))).toEqual([
      { href: 'https://example.com/a.zip', attribute: 'a.zip', download: 'mine.zip' },
    ]);
  });

  it('takes every anchor for "all" and skips an SVG anchor, whose href is not a string', () => {
    stubPage([
      fakeAnchor('https://example.com/1'),
      fakeAnchor({ baseVal: 'x' }),
      fakeAnchor('https://example.com/2'),
    ]);

    expect(collectPageLinks('all').map((l) => l.href)).toEqual([
      'https://example.com/1',
      'https://example.com/2',
    ]);
  });

  it('takes only the anchors inside the selection, and nothing for a collapsed or missing one', () => {
    const inside = fakeAnchor('https://example.com/in');
    const outside = fakeAnchor('https://example.com/out');

    stubPage([inside, outside], { collapsed: false, inside: [inside] });
    expect(collectPageLinks('selection').map((l) => l.href)).toEqual(['https://example.com/in']);

    stubPage([inside, outside], { collapsed: true, inside: [inside] });
    expect(collectPageLinks('selection')).toEqual([]);

    stubPage([inside, outside]);
    expect(collectPageLinks('selection')).toEqual([]);
    expect(collectPageLinks('all')).toHaveLength(2);
  });
});

describe('[TC-BRW-005] which links are worth sending', () => {
  it('keeps 120 absolute and relative-resolved links in order', () => {
    const anchors = Array.from({ length: 120 }, (_, i) =>
      i % 2 === 0
        ? anchor(`https://example.com/docs/file${String(i)}.pdf`, {
            attribute: `file${String(i)}.pdf`,
          })
        : anchor(`https://cdn.example.net/x/${String(i)}.zip`),
    );

    const { links, truncated } = normalizeLinks(anchors);

    expect(truncated).toBe(false);
    expect(links).toHaveLength(120);
    expect(links[0]).toEqual({ url: 'https://example.com/docs/file0.pdf', filename: 'file0.pdf' });
    expect(links[119]?.url).toBe('https://cdn.example.net/x/119.zip');
  });

  it('drops javascript:, mailto:, data:, blob:, file:, fragment-only, invalid and over-long links', () => {
    const long = `https://example.com/${'a'.repeat(8200)}`;
    const { links } = normalizeLinks([
      anchor('javascript:void(0)'),
      anchor('mailto:me@example.com'),
      anchor('data:text/plain,hi'),
      anchor('blob:https://example.com/uuid'),
      anchor('file:///c:/x.zip'),
      anchor('https://example.com/page#top', { attribute: '#top' }),
      anchor('not a url'),
      anchor(long),
      anchor('https://example.com/keep.zip'),
      anchor('ftp://files.example.com/pub/a.iso'),
    ]);

    expect(links.map((l) => l.url)).toEqual([
      'https://example.com/keep.zip',
      'ftp://files.example.com/pub/a.iso',
    ]);
  });

  it('removes duplicates and the first one wins', () => {
    const { links } = normalizeLinks([
      anchor('https://example.com/a.zip', { download: 'first.zip' }),
      anchor('https://example.com/a.zip', { download: 'second.zip' }),
      anchor('https://example.com/b.zip'),
    ]);

    expect(links).toEqual([
      { url: 'https://example.com/a.zip', filename: 'first.zip' },
      { url: 'https://example.com/b.zip', filename: 'b.zip' },
    ]);
  });

  it('names a file by the download attribute, else the decoded last path segment, else not at all', () => {
    const { links } = normalizeLinks([
      anchor('https://example.com/x/report%20final.pdf', { download: 'C:\\temp\\mine.pdf' }),
      anchor('https://example.com/x/report%20final.pdf?v=2'),
      anchor('https://example.com/dir/'),
      anchor('https://example.com/'),
      anchor('https://example.com/bad%ZZ.zip'),
    ]);

    expect(links[0]?.filename).toBe('mine.pdf');
    expect(links[1]?.filename).toBe('report final.pdf');
    expect(links[2]).toEqual({ url: 'https://example.com/dir/' });
    expect(links[3]).toEqual({ url: 'https://example.com/' });
    expect(links[4]?.filename).toBeUndefined();
  });

  it('cuts a long file name to 255 characters and keeps its extension', () => {
    const { links } = normalizeLinks([
      anchor('https://example.com/a.zip', { download: `${'n'.repeat(400)}.zip` }),
    ]);

    expect(links[0]?.filename).toHaveLength(255);
    expect(links[0]?.filename?.endsWith('.zip')).toBe(true);
  });

  it('keeps the first 100,000 links and says that the rest was dropped', () => {
    const anchors = Array.from({ length: MAX_LINKS + 5 }, (_, i) =>
      anchor(`https://example.com/${String(i)}.bin`),
    );

    const { links, truncated } = normalizeLinks(anchors);

    expect(truncated).toBe(true);
    expect(links).toHaveLength(MAX_LINKS);
  });

  it('checks what an executeScript frame returned before using it', () => {
    expect(parseRawAnchors(undefined)).toEqual([]);
    expect(parseRawAnchors('x')).toEqual([]);
    expect(
      parseRawAnchors([
        { href: 'https://a/', attribute: 'a', download: '' },
        { href: 1 },
        null,
        'x',
      ]),
    ).toEqual([{ href: 'https://a/', attribute: 'a', download: '' }]);
  });

  it('accepts only http, https, ftp and ftps, within 8,192 characters, for a single link', () => {
    expect(isDownloadableUrl('https://example.com/a.zip')).toBe(true);
    expect(isDownloadableUrl('ftp://example.com/a.zip')).toBe(true);
    expect(isDownloadableUrl('blob:https://example.com/x')).toBe(false);
    expect(isDownloadableUrl('data:text/plain,x')).toBe(false);
    expect(isDownloadableUrl(`https://example.com/${'a'.repeat(8200)}`)).toBe(false);
    expect(isDownloadableUrl(undefined)).toBe(false);
  });
});

describe('[TC-BRW-005] the batch', () => {
  const links = Array.from({ length: 40 }, (_, i) => ({
    url: `https://example.com/doc${String(i)}.pdf`,
    filename: `doc${String(i)}.pdf`,
  }));

  it('is a valid addBatch message with the page as referrer', () => {
    const batch = buildAddBatch('b1', 'https://example.com/downloads', links, 'Agent/1.0', []);

    expect(batch).toBeDefined();
    expect(validate(batch)).toBe(true);
    expect(batch?.links).toHaveLength(40);
    expect(batch?.referrer).toBe('https://example.com/downloads');
  });

  it('is not built without links or for a page that is not http or https (the schema needs a web referrer)', () => {
    expect(buildAddBatch('b', 'https://example.com/', [], 'a', [])).toBeUndefined();
    expect(buildAddBatch('b', 'file:///c:/page.html', links, 'a', [])).toBeUndefined();
    expect(buildAddBatch('b', undefined, links, 'a', [])).toBeUndefined();
  });

  it('drops the tail of a batch that would pass 32 MiB, never part of a link', () => {
    const big = Array.from({ length: 6000 }, (_, i) => ({
      url: `https://example.com/${String(i)}/${'p'.repeat(8100)}`,
      filename: 'é'.repeat(255),
    }));

    const batch = buildAddBatch('b', 'https://example.com/', big, 'a', []);

    expect(batch).toBeDefined();
    expect(batch?.links.length).toBeLessThan(6000);
    expect(new TextEncoder().encode(JSON.stringify(batch)).length).toBeLessThanOrEqual(
      MAX_BATCH_BYTES,
    );
    expect(batch?.links[0]).toEqual(big[0]);
  });

  it('collects one representative URL per origin, up to a limit, so cookies are read once per host', () => {
    const many = [
      { url: 'https://a.example/1' },
      { url: 'https://a.example/2' },
      { url: 'https://b.example/x' },
      { url: 'http://a.example/3' },
      { url: 'https://c.example/y' },
    ];

    expect(originsOf(many, 10)).toEqual([
      'https://a.example/1',
      'https://b.example/x',
      'http://a.example/3',
      'https://c.example/y',
    ]);
    expect(originsOf(many, 2)).toEqual(['https://a.example/1', 'https://b.example/x']);
  });
});
