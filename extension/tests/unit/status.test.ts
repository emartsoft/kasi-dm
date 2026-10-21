import { describe, expect, it } from 'vitest';
import {
  isConnectedReply,
  isStatusRequest,
  resolveStatus,
  STATUS_REQUEST,
} from '../../src/shared/status';

describe('status', () => {
  it('asks for permission before reporting the connection', () => {
    expect(resolveStatus(true, false)).toBe('needsPermission');
    expect(resolveStatus(false, false)).toBe('needsPermission');
  });

  it('reports the connection once permitted', () => {
    expect(resolveStatus(true, true)).toBe('connected');
    expect(resolveStatus(false, true)).toBe('unavailable');
  });

  it('recognises only its own request', () => {
    expect(isStatusRequest(STATUS_REQUEST)).toBe(true);
    expect(isStatusRequest({ kind: 'other' })).toBe(false);
    expect(isStatusRequest(null)).toBe(false);
  });
});

describe('isConnectedReply', () => {
  it('is true only for { connected: true }', () => {
    expect(isConnectedReply({ connected: true })).toBe(true);
    expect(isConnectedReply({ connected: false })).toBe(false);
    expect(isConnectedReply({ connected: 'true' })).toBe(false);
    expect(isConnectedReply(null)).toBe(false);
    expect(isConnectedReply(undefined)).toBe(false);
    expect(isConnectedReply('connected')).toBe(false);
  });
});
