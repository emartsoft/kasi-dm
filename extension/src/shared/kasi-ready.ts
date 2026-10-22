// Waiting for Kasi, shared by everything that sends a request to it (the download hand-off, the context menus).
// Pause, cookies and the ack window are each caller's business; this is only "is Kasi there, or can it be started in time".

/** The host's launch window of 5 s plus the hello and rules round trip (plan m2-4 2.2). */
export const READY_WAIT_MS = 6000;

export interface ReadyPort {
  isConnected(): boolean;
  lastFailure(): 'host-missing' | 'app-not-installed' | 'app-not-running' | undefined;
  reconnectNow(): void;
  whenConnected(timeoutMs: number): Promise<boolean>;
}

// Waits only when Kasi has to be started; a machine with no host or no Kasi installed waits for nothing.
export async function waitForKasi(port: ReadyPort): Promise<boolean> {
  if (port.isConnected()) return true;
  const failure = port.lastFailure();
  if (failure === 'host-missing' || failure === 'app-not-installed') return false;
  port.reconnectNow();
  return port.whenConnected(READY_WAIT_MS);
}
