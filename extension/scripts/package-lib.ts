// What a store package may and may not contain (plan m2-7, ADR 0022), kept apart from the command that builds the zips so tests can
// feed it faults. No dependency: reading a zip takes the central directory and node:zlib.
import { inflateRawSync } from 'node:zlib';

export type Target = 'chromium' | 'firefox';

export interface ZipEntry {
  name: string;
  method: number;
  compressedSize: number;
  localOffset: number;
}

export function packageFileName(target: Target, version: string): string {
  return `kasi-extension-${target}-${version}.zip`;
}

export function sourceFileName(version: string): string {
  return `kasi-extension-source-${version}.zip`;
}

/** Manifest keys and values that must never reach a store: a development key, a self-hosted update address, a local address. */
export function manifestProblems(manifest: Record<string, unknown>): string[] {
  const problems: string[] = [];
  if ('key' in manifest) problems.push('the manifest has a "key" (it pins a development id)');
  if ('update_url' in manifest) problems.push('the manifest has an "update_url"');
  if (manifest.manifest_version !== 3) problems.push('the manifest is not Manifest V3');
  const patterns = [
    ...((manifest.host_permissions as string[] | undefined) ?? []),
    ...((manifest.content_scripts as { matches?: string[] }[] | undefined) ?? []).flatMap(
      (script) => script.matches ?? [],
    ),
  ];
  if (patterns.some((pattern) => /localhost|127\.0\.0\.1|\[::1\]/.test(pattern))) {
    problems.push('the manifest matches a local address');
  }
  return problems;
}

/** Entries that do not belong in a distribution package. */
export function fileProblems(names: string[]): string[] {
  const problems: string[] = [];
  if (names.some((name) => name.endsWith('.map'))) problems.push('a source map is in the package');
  if (names.some((name) => /(^|\/)(tests?|node_modules|\.git)\//.test(name))) {
    problems.push('tests, node_modules or .git are in the package');
  }
  if (!names.includes('manifest.json')) problems.push('the package has no manifest.json');
  return problems;
}

export function listZip(zip: Buffer): ZipEntry[] {
  let end = -1;
  for (let i = zip.length - 22; i >= Math.max(0, zip.length - 22 - 0xffff); i--) {
    if (zip.readUInt32LE(i) === 0x06054b50) {
      end = i;
      break;
    }
  }
  if (end < 0) throw new Error('not a zip file');
  const count = zip.readUInt16LE(end + 10);
  let offset = zip.readUInt32LE(end + 16);
  const entries: ZipEntry[] = [];
  for (let i = 0; i < count; i++) {
    if (zip.readUInt32LE(offset) !== 0x02014b50) throw new Error('damaged zip directory');
    const nameLength = zip.readUInt16LE(offset + 28);
    const extraLength = zip.readUInt16LE(offset + 30);
    const commentLength = zip.readUInt16LE(offset + 32);
    entries.push({
      method: zip.readUInt16LE(offset + 10),
      compressedSize: zip.readUInt32LE(offset + 20),
      localOffset: zip.readUInt32LE(offset + 42),
      name: zip.toString('utf8', offset + 46, offset + 46 + nameLength),
    });
    offset += 46 + nameLength + extraLength + commentLength;
  }
  return entries;
}

export function readZipEntry(zip: Buffer, entry: ZipEntry): Buffer {
  const at = entry.localOffset;
  if (zip.readUInt32LE(at) !== 0x04034b50) throw new Error('damaged zip entry');
  const start = at + 30 + zip.readUInt16LE(at + 26) + zip.readUInt16LE(at + 28);
  const data = zip.subarray(start, start + entry.compressedSize);
  if (entry.method === 0) return data;
  if (entry.method === 8) return inflateRawSync(data);
  throw new Error(`zip method ${String(entry.method)} is not supported`);
}

/** Everything wrong with a built extension zip; empty means it may be uploaded. */
export function zipProblems(zip: Buffer, expectedVersion: string): string[] {
  const entries = listZip(zip);
  const names = entries.map((entry) => entry.name).filter((name) => !name.endsWith('/'));
  const problems = fileProblems(names);
  const manifestEntry = entries.find((entry) => entry.name === 'manifest.json');
  if (manifestEntry) {
    const manifest = JSON.parse(readZipEntry(zip, manifestEntry).toString('utf8')) as Record<
      string,
      unknown
    >;
    problems.push(...manifestProblems(manifest));
    if (manifest.version !== expectedVersion) {
      problems.push(`the manifest version is ${String(manifest.version)}, not ${expectedVersion}`);
    }
  }
  return problems;
}

/** What the AMO source package must hold to be rebuilt, and must not hold. */
export function sourceZipProblems(zip: Buffer): string[] {
  const names = listZip(zip)
    .map((entry) => entry.name)
    .filter((name) => !name.endsWith('/'))
    .map((name) => name.slice(name.indexOf('/') + 1)); // drop the top folder
  const problems: string[] = [];
  for (const required of [
    'extension/package.json',
    'extension/package-lock.json',
    'extension/.nvmrc',
    'extension/vite.config.ts',
    'extension/manifests/manifest.base.json',
    'docs/protocol/kasi-messages.schema.json',
    'docs/store/amo-build.md',
  ]) {
    if (!names.includes(required)) problems.push(`the source package lacks ${required}`);
  }
  if (names.some((name) => /(^|\/)(node_modules|dist)\//.test(name))) {
    problems.push('the source package holds node_modules or dist');
  }
  if (names.some((name) => name.endsWith('.map')))
    problems.push('a source map is in the source package');
  return problems;
}
