// Plan m2-7 2.3: what a store package may contain. The zips here are built in memory (stored entries), so no browser, build or git is needed.
import { deflateRawSync } from 'node:zlib';
import { describe, expect, it } from 'vitest';
import {
  fileProblems,
  listZip,
  manifestProblems,
  packageFileName,
  readZipEntry,
  sourceFileName,
  sourceZipProblems,
  zipProblems,
} from '../../scripts/package-lib';

const good = {
  manifest_version: 3,
  version: '0.2.0',
  permissions: ['downloads'],
  host_permissions: ['<all_urls>'],
  content_scripts: [{ matches: ['<all_urls>'], js: ['content.js'] }],
};

function crc32(data: Buffer): number {
  let crc = 0xffffffff;
  for (const byte of data) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}

/** A zip with one entry per file, deflated for the first and stored for the rest, so both methods are read. */
function zipOf(files: Record<string, string>): Buffer {
  const parts: Buffer[] = [];
  const directory: Buffer[] = [];
  let offset = 0;
  Object.entries(files).forEach(([name, text], index) => {
    const data = Buffer.from(text);
    const method = index === 0 ? 8 : 0;
    const body = method === 8 ? deflateRawSync(data) : data;
    const nameBytes = Buffer.from(name);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(method, 8);
    local.writeUInt32LE(crc32(data), 14);
    local.writeUInt32LE(body.length, 18);
    local.writeUInt32LE(data.length, 22);
    local.writeUInt16LE(nameBytes.length, 26);
    parts.push(local, nameBytes, body);
    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE(20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(method, 10);
    central.writeUInt32LE(crc32(data), 16);
    central.writeUInt32LE(body.length, 20);
    central.writeUInt32LE(data.length, 24);
    central.writeUInt16LE(nameBytes.length, 28);
    central.writeUInt32LE(offset, 42);
    directory.push(central, nameBytes);
    offset += 30 + nameBytes.length + body.length;
  });
  const central = Buffer.concat(directory);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(Object.keys(files).length, 8);
  end.writeUInt16LE(Object.keys(files).length, 10);
  end.writeUInt32LE(central.length, 12);
  end.writeUInt32LE(offset, 16);
  return Buffer.concat([...parts, central, end]);
}

const manifestText = (over: Record<string, unknown> = {}): string =>
  JSON.stringify({ ...good, ...over });

describe('package names', () => {
  it('carry the target and the version from the manifest', () => {
    expect(packageFileName('chromium', '0.2.0')).toBe('kasi-extension-chromium-0.2.0.zip');
    expect(packageFileName('firefox', '1.0.0')).toBe('kasi-extension-firefox-1.0.0.zip');
    expect(sourceFileName('0.2.0')).toBe('kasi-extension-source-0.2.0.zip');
  });
});

describe('manifest problems', () => {
  it('accepts the shipped shape', () => {
    expect(manifestProblems(good)).toEqual([]);
  });

  it('refuses a development key, an update address, a local address and Manifest V2', () => {
    expect(manifestProblems({ ...good, key: 'MIIB' })).toEqual([expect.stringContaining('"key"')]);
    expect(manifestProblems({ ...good, update_url: 'https://example.org/u.xml' })).toEqual([
      expect.stringContaining('update_url'),
    ]);
    expect(manifestProblems({ ...good, host_permissions: ['http://localhost/*'] })).toEqual([
      expect.stringContaining('local address'),
    ]);
    expect(
      manifestProblems({ ...good, content_scripts: [{ matches: ['http://127.0.0.1/*'] }] }),
    ).toEqual([expect.stringContaining('local address')]);
    expect(manifestProblems({ ...good, manifest_version: 2 })).toEqual([
      expect.stringContaining('Manifest V3'),
    ]);
  });
});

describe('zip problems', () => {
  it('lets a clean package through, reading deflated and stored entries', () => {
    const zip = zipOf({ 'manifest.json': manifestText(), 'background.js': '/* code */' });

    expect(zipProblems(zip, '0.2.0')).toEqual([]);
    const entries = listZip(zip);
    expect(entries.map((e) => e.name)).toEqual(['manifest.json', 'background.js']);
    expect(readZipEntry(zip, entries[1] as (typeof entries)[number]).toString()).toBe('/* code */');
  });

  it('refuses a package with a development key in its manifest', () => {
    const zip = zipOf({ 'manifest.json': manifestText({ key: 'MIIBIjANBgkq' }) });

    expect(zipProblems(zip, '0.2.0')).toEqual([expect.stringContaining('"key"')]);
  });

  it('refuses a source map', () => {
    const zip = zipOf({ 'manifest.json': manifestText(), 'background.js.map': '{}' });

    expect(zipProblems(zip, '0.2.0')).toEqual([expect.stringContaining('source map')]);
  });

  it('refuses tests, node_modules and a missing manifest', () => {
    expect(fileProblems(['manifest.json', 'tests/unit/a.test.ts'])).toHaveLength(1);
    expect(fileProblems(['manifest.json', 'node_modules/x/index.js'])).toHaveLength(1);
    expect(fileProblems(['background.js'])).toEqual([expect.stringContaining('no manifest.json')]);
  });

  it('refuses a package whose manifest version is not the expected one', () => {
    const zip = zipOf({ 'manifest.json': manifestText({ version: '0.1.0' }) });

    expect(zipProblems(zip, '0.2.0')).toEqual([expect.stringContaining('0.1.0, not 0.2.0')]);
  });

  it('refuses something that is not a zip', () => {
    expect(() => zipProblems(Buffer.from('not a zip'), '0.2.0')).toThrow('not a zip');
  });
});

describe('source package problems', () => {
  const required = {
    'kasi-extension-source-0.2.0/extension/package.json': '{}',
    'kasi-extension-source-0.2.0/extension/package-lock.json': '{}',
    'kasi-extension-source-0.2.0/extension/.nvmrc': '22',
    'kasi-extension-source-0.2.0/extension/vite.config.ts': '',
    'kasi-extension-source-0.2.0/extension/manifests/manifest.base.json': '{}',
    'kasi-extension-source-0.2.0/docs/protocol/kasi-messages.schema.json': '{}',
    'kasi-extension-source-0.2.0/docs/store/amo-build.md': '#',
  };

  it('accepts a source package that can be rebuilt', () => {
    expect(sourceZipProblems(zipOf(required))).toEqual([]);
  });

  it('names what is missing and refuses node_modules and dist', () => {
    const rest = Object.fromEntries(
      Object.entries(required).filter(([name]) => !name.endsWith('.nvmrc')),
    );

    expect(sourceZipProblems(zipOf(rest))).toEqual([expect.stringContaining('.nvmrc')]);
    expect(
      sourceZipProblems(
        zipOf({ ...required, 'kasi-extension-source-0.2.0/extension/dist/a.js': '' }),
      ),
    ).toEqual([expect.stringContaining('node_modules or dist')]);
    expect(
      sourceZipProblems(
        zipOf({ ...required, 'kasi-extension-source-0.2.0/extension/node_modules/a/i.js': '' }),
      ),
    ).toEqual([expect.stringContaining('node_modules or dist')]);
  });
});
