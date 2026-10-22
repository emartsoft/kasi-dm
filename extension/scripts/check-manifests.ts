import { access, readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const storeIds = JSON.parse(await readFile(resolve(root, 'store-ids.json'), 'utf8')) as Record<
  'chrome' | 'edge' | 'firefox',
  string[]
>;
const expectedPermissions = [
  'downloads',
  'cookies',
  'webRequest',
  'contextMenus',
  'nativeMessaging',
  'storage',
  'scripting',
];

for (const target of ['chromium', 'firefox'] as const) {
  const output = resolve(root, 'dist', target);
  const manifest = JSON.parse(await readFile(resolve(output, 'manifest.json'), 'utf8')) as Record<
    string,
    unknown
  >;

  if (
    manifest.manifest_version !== 3 ||
    manifest.default_locale !== 'en' ||
    JSON.stringify(manifest.permissions) !== JSON.stringify(expectedPermissions) ||
    JSON.stringify(manifest.host_permissions) !== JSON.stringify(['<all_urls>'])
  ) {
    throw new Error(`${target} manifest has an invalid shared contract`);
  }

  const background = manifest.background as Record<string, unknown> | undefined;
  if (!background) {
    throw new Error(`${target} manifest has no background`);
  }

  const gecko = (manifest.browser_specific_settings as Record<string, unknown> | undefined)
    ?.gecko as Record<string, unknown> | undefined;
  const invalidBackground =
    target === 'chromium'
      ? background.service_worker !== 'background.js' ||
        'scripts' in background ||
        typeof manifest.minimum_chrome_version !== 'string' ||
        'browser_specific_settings' in manifest
      : JSON.stringify(background.scripts) !== JSON.stringify(['background.js']) ||
        'service_worker' in background ||
        typeof gecko?.strict_min_version !== 'string' ||
        gecko.id !== storeIds.firefox[0];

  if (invalidBackground) {
    throw new Error(`${target} manifest has an invalid background`);
  }

  const action = manifest.action as {
    default_popup: string;
    default_icon: Record<string, string>;
  };
  const options = manifest.options_ui as { page: string };
  const contentScripts = manifest.content_scripts as { js: string[] }[];
  const icons = manifest.icons as Record<string, string>;
  const assets = [
    'background.js',
    ...contentScripts.flatMap((script) => script.js),
    action.default_popup,
    options.page,
    '_locales/en/messages.json',
    ...Object.values(icons),
    ...Object.values(action.default_icon),
  ];
  for (const asset of assets) {
    await access(resolve(output, asset));
  }
}

for (const id of [...storeIds.chrome, ...storeIds.edge]) {
  if (!/^[a-p]{32}$/.test(id))
    throw new Error('store-ids.json holds an invalid Chromium extension id');
}

console.log('Chromium and Firefox manifests and packaged assets are valid.');
