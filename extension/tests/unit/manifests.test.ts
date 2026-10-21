import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

function manifest(name: string): Record<string, unknown> {
  const read = (file: string) =>
    JSON.parse(readFileSync(new URL(`../../manifests/${file}`, import.meta.url), 'utf8')) as Record<
      string,
      unknown
    >;
  return { ...read('manifest.base.json'), ...read(name) };
}

describe('merged manifests', () => {
  const chromium = manifest('manifest.chromium.json');
  const firefox = manifest('manifest.firefox.json');

  it('share permissions', () => {
    expect(chromium.permissions).toEqual(firefox.permissions);
    expect(chromium.host_permissions).toEqual(firefox.host_permissions);
    expect(chromium.manifest_version).toBe(3);
    expect(firefox.manifest_version).toBe(3);
  });

  it('use the right background form', () => {
    expect(chromium.background).toEqual({ service_worker: 'background.js' });
    expect(firefox.background).toEqual({ scripts: ['background.js'] });
  });

  it('keep Firefox-only keys out of Chromium and declare data collection for AMO', () => {
    expect(chromium).not.toHaveProperty('browser_specific_settings');
    expect(firefox.browser_specific_settings).toMatchObject({
      gecko: { data_collection_permissions: { required: ['none'] } },
    });
  });
});
