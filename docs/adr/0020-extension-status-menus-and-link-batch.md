# 0020. Extension status, interception toggle, menus and the link batch

- Status: Proposed (2.5a, status and the toggle; 2.5b, the menus and the link batch; 2.5c extends it with `refreshLink`)
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

**Context menus and the link batch** (M2.5b, KASI-BRW-004, 005). Three menus, created with fixed ids: "Download with Kasi" (link, image, video, audio), "Download all links" (page) and "Download selected links" (selection). They are created in `runtime.onInstalled` and once per browser session at start-up (a flag in `storage.local` is wrong because it would outlive a browser restart; `storage.session` is used), always after `removeAll()`, so overlapping calls never duplicate a menu. A click never pauses anything; it waits for Kasi exactly like a download does (`kasi-ready.ts`: no wait when no host or no Kasi is installed, up to 6 s while the host starts Kasi) and sends one `addDownload` or one `addBatch`; a refusal, a timeout or an unreachable Kasi shows the red `!` badge. The menus do not depend on the interception toggle: a user who switched interception off still has them.
- *Collecting links.* The plan said the content script would answer a `kasi-collect` message per frame. `scripting.executeScript` with `allFrames` does the same with no content-script change and returns one result per frame, so that is what is used: a small, self-contained page function (`collectPageLinks`, serialised by the browser; a test evaluates its source in an empty context to keep it free of imports) returns raw anchors, and `normalizeLinks` in the background keeps http, https and ftp links of at most 8,192 characters, drops fragment-only links, `javascript:`, `mailto:`, `data:`, `blob:` and `file:`, removes duplicates (the first wins) and stops at 100,000. The batch is cut at 32 MiB serialised (the browser may send 64 MiB to the host). The file name hint is the `download` attribute, else the last path segment; no link text crosses the protocol (plan decision 1).
- *Cookies* for a batch come from the page and from one representative URL per distinct origin, at most 50 origins; a page of links to hundreds of sites sends the cookies of the first ones. A single link sends the cookies for that URL. Nothing is logged but counts and codes.
- *The test seam.* A native context menu cannot be clicked by Playwright or WebDriver, so the background also accepts a `kasi-menu-click` message carrying the data of a click and hands it to the same handler. It is accepted only from a page of this extension (`sender.url` under the extension's own origin): never from a content script, so never from a web page. A real right-click stays a manual row in `docs/qa/browser-interception.md`.
- *`mime`.* A link click has no MIME type; `addDownload` carries `application/octet-stream` and Kasi resolves the real type and name from the response.

## Consequences

- After the user quits Kasi nothing starts it until a matching download, a browser start or (after a crash, not a quit) the popup.
- A matching download right after a quit waits for the host to start Kasi (up to the 6 s ready budget) as before.
- A send that fails because Kasi quit at that instant can still reconnect once through the launcher; that is the single remaining path that may start Kasi without a new reason.
- The popup toggle and the `off` badge are local state; no protocol change.
