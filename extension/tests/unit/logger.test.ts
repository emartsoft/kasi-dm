import { describe, expect, it, vi } from 'vitest';
import { createLogger, redact } from '../../src/shared/logger';

function captured() {
  const sink = { debug: vi.fn(), info: vi.fn(), warn: vi.fn(), error: vi.fn() };
  const text = () => JSON.stringify(sink.warn.mock.calls);
  return { logger: createLogger(sink), text };
}

describe('logger redaction', () => {
  it('drops query strings, fragments and userinfo from URLs in messages', () => {
    const { logger, text } = captured();
    logger.warn('failed https://user:pw@files.example.com/a/b.zip?token=SECRET&x=1#frag now');
    expect(text()).toContain('https://files.example.com/a/b.zip');
    expect(text()).not.toMatch(/SECRET|token=|user:pw|frag/);
  });

  it('drops query strings from URLs nested in context', () => {
    const { logger, text } = captured();
    logger.warn('download', { url: 'https://x.example/f?sig=ABC123', list: ['ftp://h/p?k=v'] });
    expect(text()).toContain('https://x.example/f');
    expect(text()).not.toMatch(/ABC123|k=v|sig=/);
  });

  it('redacts cookie arrays and secret-named keys', () => {
    const { logger, text } = captured();
    logger.warn('add', {
      cookies: [
        {
          name: 'sid',
          value: 'COOKIEVAL',
          domain: 'a.com',
          path: '/',
          secure: true,
          httpOnly: true,
        },
      ],
      headers: { Authorization: 'Bearer TOKENVAL' },
      apiToken: 'TOKENVAL2',
      nested: { cookie: 'COOKIEVAL2', deep: { value: 'COOKIEVAL3' } },
    });
    expect(text()).not.toMatch(/COOKIEVAL|TOKENVAL|sid/);
  });

  it('keeps only name and redacted message of nested error objects', () => {
    const inner = new Error(
      'GET https://a.example/x?token=SECRET failed at C:\\Users\\bob\\file.zip',
    );
    const text = JSON.stringify(redact({ outer: { error: inner } }));
    expect(text).toContain('https://a.example/x');
    expect(text).not.toMatch(/SECRET|bob|stack/);
  });

  it('survives cycles', () => {
    const loop: Record<string, unknown> = {};
    loop.self = loop;
    expect(JSON.stringify(redact(loop))).toContain('[truncated]');
  });

  it('leaves harmless values alone', () => {
    expect(redact({ n: 1, ok: true, s: 'hello' })).toEqual({ n: 1, ok: true, s: 'hello' });
  });
});
