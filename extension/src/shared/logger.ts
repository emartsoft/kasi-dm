// Redacting logger (CLAUDE.md rule 3): never writes query strings, cookies, headers, tokens or user paths.
const SECRET_KEY = /cookie|authorization|token|secret|password|headers?|^value$/i;
const URL_IN_TEXT = /\b[a-z][a-z0-9+.-]*:\/\/[^\s"'<>)]+/gi;
const WINDOWS_PATH = /\b[a-z]:\\[^\s"'<>]*/gi;
const MAX_DEPTH = 6;

export type LogContext = Record<string, unknown>;
export interface LogSink {
  debug(...args: unknown[]): void;
  info(...args: unknown[]): void;
  warn(...args: unknown[]): void;
  error(...args: unknown[]): void;
}

function stripUrl(url: string): string {
  const withoutUserinfo = url.replace(/^([a-z][a-z0-9+.-]*:\/\/)[^/?#@]*@/i, '$1');
  return withoutUserinfo.replace(/[?#].*$/, '');
}

export function redactString(text: string): string {
  return text.replace(URL_IN_TEXT, stripUrl).replace(WINDOWS_PATH, '[path]');
}

export function redact(value: unknown, depth = 0, seen = new WeakSet()): unknown {
  if (typeof value === 'string') return redactString(value);
  if (typeof value !== 'object' || value === null) return value;
  if (depth >= MAX_DEPTH || seen.has(value)) return '[truncated]';
  seen.add(value);

  // Stacks carry file paths, so an error keeps only its name and redacted message.
  if (value instanceof Error) {
    return { name: value.name, message: redactString(value.message) };
  }
  if (Array.isArray(value)) return value.map((item) => redact(item, depth + 1, seen));

  const result: Record<string, unknown> = {};
  for (const [key, item] of Object.entries(value)) {
    result[key] = SECRET_KEY.test(key) ? '[redacted]' : redact(item, depth + 1, seen);
  }
  return result;
}

export function createLogger(sink: LogSink = console) {
  const write =
    (level: keyof LogSink) =>
    (message: string, context?: LogContext): void => {
      const text = `[kasi] ${redactString(message)}`;
      if (context === undefined) sink[level](text);
      else sink[level](text, redact(context));
    };
  return {
    debug: write('debug'),
    info: write('info'),
    warn: write('warn'),
    error: write('error'),
  };
}

export type Logger = ReturnType<typeof createLogger>;
