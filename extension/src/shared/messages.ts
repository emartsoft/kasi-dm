// Runtime checks for messages that arrive from Kasi. Ajv is not used here: it compiles schemas with
// new Function(), which the MV3 content security policy forbids. tests/unit checks these against the schema fixtures.
import type { Ack, ErrorMessage, Rules } from '../types/protocol';

type Obj = Record<string, unknown>;

const BYPASS_KEYS = ['alt', 'ctrl', 'shift', 'none'];
const ERROR_CODES = [
  'INVALID_MESSAGE',
  'UNSUPPORTED_VERSION',
  'APP_NOT_RUNNING',
  'APP_NOT_INSTALLED',
  'INTERNAL',
];

function isObj(value: unknown): value is Obj {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function hasOnly(value: Obj, keys: string[]): boolean {
  return Object.keys(value).every((key) => keys.includes(key));
}

function isText(value: unknown, max: number): value is string {
  return typeof value === 'string' && value.length >= 1 && value.length <= max;
}

function isList(value: unknown): value is string[] {
  return Array.isArray(value) && value.length <= 1000 && value.every((item) => isText(item, 256));
}

function isHeader(value: Obj, type: string): boolean {
  return value.v === 1 && value.type === type && isText(value.id, 64);
}

export function parseAck(value: unknown): Ack | undefined {
  if (!isObj(value) || !hasOnly(value, ['v', 'id', 'type', 'ok', 'detail'])) return undefined;
  if (!isHeader(value, 'ack') || value.ok !== true) return undefined;
  if (value.detail !== undefined && !isText(value.detail, 256)) return undefined;
  return value as unknown as Ack;
}

export function parseError(value: unknown): ErrorMessage | undefined {
  if (!isObj(value) || !hasOnly(value, ['v', 'id', 'type', 'code', 'detail'])) return undefined;
  if (!isHeader(value, 'error') || !ERROR_CODES.includes(value.code as string)) return undefined;
  if (value.detail !== undefined && !isText(value.detail, 256)) return undefined;
  return value as unknown as ErrorMessage;
}

export function parseRules(value: unknown): Rules | undefined {
  const keys = [
    'v',
    'id',
    'type',
    'enabled',
    'extensions',
    'mimeTypes',
    'excludedSites',
    'bypassKey',
    'ackTimeoutMs',
    'minSizeBytes',
  ];
  if (!isObj(value) || !hasOnly(value, keys) || !isHeader(value, 'rules')) return undefined;
  const timeout = value.ackTimeoutMs;
  const valid =
    typeof value.enabled === 'boolean' &&
    isList(value.extensions) &&
    isList(value.mimeTypes) &&
    isList(value.excludedSites) &&
    BYPASS_KEYS.includes(value.bypassKey as string) &&
    typeof timeout === 'number' &&
    Number.isInteger(timeout) &&
    timeout >= 100 &&
    timeout <= 60000 &&
    (value.minSizeBytes === undefined ||
      (Number.isSafeInteger(value.minSizeBytes) && (value.minSizeBytes as number) >= 0));
  return valid ? (value as unknown as Rules) : undefined;
}
