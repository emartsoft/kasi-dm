import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { parseRules } from '../../src/shared/messages';
import { createRulesCache, type StorageAreaLike } from '../../src/shared/rules-cache';

function memoryArea(): StorageAreaLike & { data: Record<string, unknown> } {
  const data: Record<string, unknown> = {};
  return {
    data,
    get: (key) => Promise.resolve(key in data ? { [key]: data[key] } : {}),
    set: (items) => {
      Object.assign(data, items);
      return Promise.resolve();
    },
  };
}

const rules = parseRules(
  JSON.parse(
    readFileSync(new URL('../../../tests/fixtures/ipc/valid/rules.json', import.meta.url), 'utf8'),
  ),
);

describe('rules cache', () => {
  it('returns nothing when empty', async () => {
    expect(await createRulesCache(memoryArea()).load()).toBeUndefined();
  });

  it('round-trips the pushed rules', async () => {
    expect(rules).toBeDefined();
    const cache = createRulesCache(memoryArea());
    if (rules) await cache.save(rules);
    expect(await cache.load()).toEqual(rules);
  });

  it('ignores malformed stored data', async () => {
    const area = memoryArea();
    area.data.rules = { type: 'rules', enabled: 'yes' };
    expect(await createRulesCache(area).load()).toBeUndefined();
  });
});
