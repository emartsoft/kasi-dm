export type ConnectionStatus = 'connected' | 'unavailable' | 'needsPermission';

// Internal request from the popup to the background page.
export const STATUS_REQUEST = { kind: 'kasi-status' } as const;

export function isStatusRequest(value: unknown): boolean {
  return (
    typeof value === 'object' &&
    value !== null &&
    'kind' in value &&
    value.kind === STATUS_REQUEST.kind
  );
}

// A missing host permission (Firefox lets users deny or revoke it) is shown before the connection state.
export function resolveStatus(connected: boolean, hasAllSites: boolean): ConnectionStatus {
  if (!hasAllSites) return 'needsPermission';
  return connected ? 'connected' : 'unavailable';
}

// The background replies to STATUS_REQUEST with { connected: boolean }; this is the one place that reads that shape.
export function isConnectedReply(reply: unknown): boolean {
  return (
    typeof reply === 'object' && reply !== null && 'connected' in reply && reply.connected === true
  );
}
