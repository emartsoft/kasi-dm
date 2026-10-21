import { readFile, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'vite';

type BrowserTarget = 'chromium' | 'firefox';

const root = fileURLToPath(new URL('.', import.meta.url));
const targets: BrowserTarget[] = ['chromium', 'firefox'];

async function readManifest(name: string): Promise<Record<string, unknown>> {
  return JSON.parse(await readFile(resolve(root, 'manifests', name), 'utf8')) as Record<
    string,
    unknown
  >;
}

async function buildTarget(target: BrowserTarget): Promise<void> {
  const outDir = resolve(root, 'dist', target);

  // The HTML build owns the clean output directory and copies localized strings.
  await build({
    configFile: false,
    root,
    base: './',
    build: {
      outDir,
      emptyOutDir: true,
      rollupOptions: {
        input: {
          popup: resolve(root, 'src/popup/index.html'),
          options: resolve(root, 'src/options/index.html'),
        },
      },
    },
  });

  // MV3 content scripts and both background forms must be classic scripts.
  for (const entry of ['background', 'content'] as const) {
    await build({
      configFile: false,
      root,
      publicDir: false,
      build: {
        outDir,
        emptyOutDir: false,
        lib: {
          entry: resolve(root, `src/${entry}/index.ts`),
          formats: ['iife'],
          name: `Kasi${entry.charAt(0).toUpperCase()}${entry.slice(1)}`,
          fileName: () => `${entry}.js`,
        },
      },
    });
  }

  const common = await readManifest('manifest.base.json');
  const specific = await readManifest(`manifest.${target}.json`);
  const manifest = { ...common, ...specific };
  await writeFile(resolve(outDir, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
}

const requested = process.argv[2];
if (requested !== undefined && !targets.includes(requested as BrowserTarget)) {
  throw new Error(`Unknown browser target: ${requested}`);
}

for (const target of requested === undefined ? targets : [requested as BrowserTarget]) {
  await buildTarget(target);
}
