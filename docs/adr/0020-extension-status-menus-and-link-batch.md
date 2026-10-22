# 0020. Extension status, interception toggle, menus and the link batch

- Status: Proposed (2.5a, status and the toggle; 2.5b and 2.5c extend it with the menus, the link batch and `refreshLink`)
- Date: 2026-10-22
- Requirements: KASI-BRW-006, KASI-BRW-004, KASI-BRW-005
- Related: `docs/plans/m2-5.md`; ADR 0017, ADR 0019

## Context

BRW-006 asks for the connection status to turn red within 5 s. A poll cannot deliver that: with the host missing the port dies at once, and when the user quits Kasi the host used to reconnect by itself, relaunching Kasi and keeping the browser port open, so the extension still showed "connected" (ADR 0019, known gap; plan m2-4 G8). A `setInterval` also does not survive a sleeping MV3 worker.

## Decision

1. **The host reports a quit and exits.** On a lost pipe the host writes one `error APP_NOT_RUNNING` (id `unknown`) and exits with code 3; it does not launch Kasi (ADR 0017). The reconnect-once for a *send* that fails stays.
2. **The extension is told, not polling.** An unsolicited `APP_NOT_RUNNING` or `APP_NOT_INSTALLED`, or the death of a port that was connected, marks it disconnected and sets the red `!` badge in the same tick (a fake-timer unit test is the 5 s statement). The port manager then does **not** reconnect on a timer: a connection that was lost waits for a reason. Reasons: the next matching download (the ready phase of ADR 0019), the popup being opened, a browser start. A connection that never came up (no host, Kasi not installed) keeps the 1 s to 30 s backoff.
3. **The popup does not bring Kasi back after a quit.** Opening the popup reconnects unless the last failure is `app-not-running` (the user quit Kasi): otherwise looking at the status would start the program the user just closed. This narrows the plan's "popup is a reason"; a download still reconnects.
4. **The popup asks the background for the state once a second while it is open** and re-renders (the popup page is alive, so a timer is safe there).
5. **Interception toggle.** `interceptionEnabled` (boolean, default true) in `storage.local`, flipped from the popup, read in `interception.handle` after the claims (so a refresh watcher still runs) and before the rules. The badge shows `off` while it is off (`!` wins). Menus will not depend on it. TRD 9.1 says the extension stores this flag besides the rules cache.

## Consequences

- After the user quits Kasi nothing starts it until a matching download, a browser start or (after a crash, not a quit) the popup.
- A matching download right after a quit waits for the host to start Kasi (up to the 6 s ready budget) as before.
- A send that fails because Kasi quit at that instant can still reconnect once through the launcher; that is the single remaining path that may start Kasi without a new reason.
- The popup toggle and the `off` badge are local state; no protocol change.
