// npm run package: builds both targets, refuses a package a store would reject, and writes the zips to dist/packages (plan m2-7 2.3).
// --skip-build reuses dist (the tests and a second run do).
import { execFileSync } from 'node:child_process';
import { mkdir, readdir, readFile, rm } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  fileProblems,
  manifestProblems,
  packageFileName,
  sourceFileName,
  sourceZipProblems,
  zipProblems,
  type Target,
} from './package-lib';

const root = fileURLToPath(new URL('..', import.meta.url));
const out = resolve(root, 'dist', 'packages');
const npm = process.platform === 'win32' ? 'npm.cmd' : 'npm';

async function filesUnder(dir: string, prefix = ''): Promise<string[]> {
  const found: string[] = [];
  for (const entry of await readdir(resolve(dir, prefix), { withFileTypes: true })) {
    const name = prefix === '' ? entry.name : `${prefix}/${entry.name}`;
    if (entry.isDirectory()) found.push(...(await filesUnder(dir, name)));
    else found.push(name);
  }
  return found;
}

function fail(what: string, problems: string[]): never {
  throw new Error(`${what}:\n  - ${problems.join('\n  - ')}`);
}

const version = (
  JSON.parse(await readFile(resolve(root, 'package.json'), 'utf8')) as { version: string }
).version;

if (!process.argv.includes('--skip-build')) {
  // shell: npm.cmd needs it on Windows
  execFileSync(npm, ['run', 'build'], {
    cwd: root,
    stdio: 'inherit',
    shell: process.platform === 'win32',
  });
}

await rm(out, { recursive: true, force: true });
await mkdir(out, { recursive: true });

for (const target of ['chromium', 'firefox'] as Target[]) {
  const dir = resolve(root, 'dist', target);
  const manifest = JSON.parse(await readFile(resolve(dir, 'manifest.json'), 'utf8')) as Record<
    string,
    unknown
  >;
  const problems = [...manifestProblems(manifest), ...fileProblems(await filesUnder(dir))];
  if (manifest.version !== version) {
    problems.push(`the ${target} manifest version ${String(manifest.version)} is not ${version}`);
  }
  if (problems.length > 0) fail(`dist/${target} cannot be packaged`, problems);

  execFileSync(
    process.execPath,
    [
      resolve(root, 'node_modules/web-ext/bin/web-ext.js'),
      'build',
      '--source-dir',
      dir,
      '--artifacts-dir',
      out,
      '--filename',
      packageFileName(target, version),
      '--overwrite-dest',
    ],
    { cwd: root, stdio: 'inherit' },
  );
  const built = zipProblems(
    await readFile(resolve(out, packageFileName(target, version))),
    version,
  );
  if (built.length > 0) fail(`the ${target} package is not acceptable`, built);
}

// The AMO source package: tracked files only (no node_modules, no dist), the schema the types are generated from, and the build steps.
const repo = execFileSync('git', ['rev-parse', '--show-toplevel'], { cwd: root }).toString().trim();
const sourceZip = resolve(out, sourceFileName(version));
execFileSync(
  'git',
  [
    'archive',
    '--format=zip',
    `--prefix=kasi-extension-source-${version}/`,
    '-o',
    sourceZip,
    'HEAD',
    'extension',
    'docs/protocol',
    'docs/store/amo-build.md',
  ],
  { cwd: repo, stdio: 'inherit' },
);
const sourceProblems = sourceZipProblems(await readFile(sourceZip));
if (sourceProblems.length > 0) fail('the source package is not acceptable', sourceProblems);

console.log(`Packages written to ${out}`);
