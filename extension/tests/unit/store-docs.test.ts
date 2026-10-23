// Plan m2-7 2.2: the text given to the stores must match the code. A permission cannot be added to the manifest without a
// justification, and the listing's name and short description are the extension's own strings.
import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

const doc = (path: string): string =>
  readFileSync(new URL(`../../../docs/${path}`, import.meta.url), 'utf8').replaceAll('
', '
'); // a Windows checkout has CRLF
const json = (url: string): unknown =>
  JSON.parse(readFileSync(new URL(url, import.meta.url), 'utf8'));

const manifest = json('../../manifests/manifest.base.json') as {
  permissions: string[];
  host_permissions: string[];
};
const messages = json('../../public/_locales/en/messages.json') as Record<
  string,
  { message: string }
>;
const justifications = doc('store/permission-justifications.md');
const headings = [...justifications.matchAll(/^### (.+)$/gm)].map((match) => match[1]);

describe('permission justifications', () => {
  it('have an entry for every permission in the manifest', () => {
    for (const permission of manifest.permissions) {
      expect(headings, `${permission} has no justification`).toContain(`\`${permission}\``);
    }
  });

  it('have no entry for a permission the manifest does not ask for', () => {
    const known = new Set([
      ...manifest.permissions.map((permission) => `\`${permission}\``),
      ...manifest.host_permissions.map((permission) => `Host permission \`${permission}\``),
      'Content script (all pages, all frames, at document start)',
      'Remote code',
    ]);
    for (const heading of headings) {
      expect(known, `${String(heading)} is not in the manifest`).toContain(heading);
    }
  });

  it('cover the host permission, the content script and the remote code answer', () => {
    expect(headings).toContain('Host permission `<all_urls>`');
    expect(headings).toContain('Content script (all pages, all frames, at document start)');
    expect(headings).toContain('Remote code');
  });

  it('name code that exists', () => {
    for (const [, file] of justifications.matchAll(/`(src\/[A-Za-z0-9/._-]+\.ts)`/g)) {
      expect(
        () => readFileSync(new URL(`../../${String(file)}`, import.meta.url)),
        `${String(file)} does not exist`,
      ).not.toThrow();
    }
  });
});

describe('listing', () => {
  const listing = doc('store/listing/en-US.md');

  it('uses the extension name and short description from its own strings', () => {
    const name = messages.extensionName?.message ?? '';
    const description = messages.extensionDescription?.message ?? '';

    expect(listing).toContain(`## Name\n\n${name}\n`);
    expect(listing).toContain(`## Short description (132 characters at most)\n\n${description}\n`);
    expect(description.length).toBeLessThanOrEqual(132);
  });
});

describe('privacy policy', () => {
  const policy = doc('legal/privacy-policy.md');

  it('says what the extension does not do', () => {
    expect(policy).toMatch(/does not send anything to MartSoft Solutions/);
    expect(policy).toMatch(/no remote code/);
    expect(policy).toMatch(/DPAPI/);
  });

  it('is the only place that still needs the owner (the contact address)', () => {
    expect(policy.match(/<owner to fill in[^>]*>/g)).toHaveLength(1);
  });
});
