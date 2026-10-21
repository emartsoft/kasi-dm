import { readdirSync, readFileSync } from 'node:fs';
import { Ajv } from 'ajv';
import { describe, expect, it } from 'vitest';
import { parseAck, parseError, parseRules } from '../../src/shared/messages';

// Shared with the C# suite (KASI-NFR-010, TPL 5.3): both sides must agree on every file.
const fixtures = new URL('../../../tests/fixtures/ipc/', import.meta.url);
const schema = JSON.parse(
  readFileSync(
    new URL('../../../docs/protocol/kasi-messages.schema.json', import.meta.url),
    'utf8',
  ),
) as object;
const validate = new Ajv({ allErrors: true, strict: true }).compile(schema);

function files(kind: 'valid' | 'invalid'): [string, string][] {
  const dir = new URL(`${kind}/`, fixtures);
  return readdirSync(dir)
    .filter((name) => name.endsWith('.json'))
    .map((name) => [name, readFileSync(new URL(name, dir), 'utf8')]);
}

// JSON.parse keeps the last of two equal keys silently; the protocol rejects the message.
function hasDuplicateKeys(text: string): boolean {
  const scopes: (Set<string> | null)[] = [];
  const token = /"(?:[^"\\]|\\.)*"|[{}[\]:,]|[^\s"{}[\]:,]+/g;
  let previous = '';
  for (const [t] of text.matchAll(token)) {
    if (t === '{') scopes.push(new Set());
    else if (t === '[') scopes.push(null);
    else if (t === '}' || t === ']') scopes.pop();
    else if (t === ':' && previous.startsWith('"')) {
      const scope = scopes[scopes.length - 1];
      if (scope?.has(previous)) return true;
      scope?.add(previous);
    }
    if (t !== ',') previous = t;
  }
  return false;
}

// The schema cannot compare two fields; the C# parser checks protocolMax >= protocolMin (see the schema description).
function rangeReversed(message: unknown): boolean {
  const hello = message as { type?: unknown; protocolMin?: number; protocolMax?: number };
  return hello.type === 'hello' && (hello.protocolMax ?? 0) < (hello.protocolMin ?? 0);
}

function accepted(text: string): boolean {
  if (hasDuplicateKeys(text)) return false;
  try {
    const message: unknown = JSON.parse(text);
    return validate(message) && !rangeReversed(message);
  } catch {
    return false;
  }
}

describe('shared IPC fixtures against the schema', () => {
  it('has fixtures to check', () => {
    expect(files('valid').length).toBeGreaterThan(10);
    expect(files('invalid').length).toBeGreaterThan(30);
  });

  it.each(files('valid'))('accepts valid/%s', (_name, text) => {
    expect(accepted(text)).toBe(true);
  });

  it.each(files('invalid'))('rejects invalid/%s', (_name, text) => {
    expect(accepted(text)).toBe(false);
  });
});

describe('runtime parsers agree with the schema for messages Kasi sends', () => {
  const parsers: Record<string, (value: unknown) => unknown> = {
    rules: parseRules,
    ack: parseAck,
    error: parseError,
  };

  for (const kind of ['valid', 'invalid'] as const) {
    it.each(files(kind))(`${kind}/%s`, (name, text) => {
      const parser = parsers[name.split(/[-.]/)[0] ?? ''];
      if (!parser || hasDuplicateKeys(text)) return;
      expect(parser(JSON.parse(text)) !== undefined).toBe(kind === 'valid');
    });
  }
});
