# 0023. Desktop shell, browser handlers and single instance

- Status: Proposed (start-up order, single instance and the UI batch dispatcher in M2.8a; the browser handlers, tray, dialogs and picker follow in 2.8b to 2.8e, which finish this ADR)
- Date: 2026-10-22
- Requirements: KASI-SYS-005, KASI-NFR-003, KASI-NFR-006, KASI-NFR-010
- Related: `docs/plans/m2-8.md` (sections 2.1, 2.3, 2.4, decision 1); ADR 0016, ADR 0017, ADR 0019

## Context

The native messaging host starts `Kasi.exe --tray` and gives up on the pipe after 5 s (ADR 0017), and it does so again on every reconnect. The pipe therefore has to be open before the engine and the UI exist, a second launch has to be silent and instant, and a URL given to a second launch has to reach the running one. The browser pipe cannot carry that: it demands a `hello` from a registered host and has no "open this URL" message.

## Decision

**Start-up order** (`Kasi.App/Startup/AppStartup.cs`, called by `Program.Main`, which stays `[STAThread]` and a `WinExe`): (1) parse the arguments; (2) take the per-user lock, the mutex `Local\Kasi-<SID>` (session namespace, not `Global\`); (3) if another instance holds it, send the hand-off and exit 0 at once, building nothing; (4) start the `KasiPipeServer`, then the hand-off server; (5) only then build and start the generic host and the engine; (6) start Avalonia, with no window when `--tray`. A failed pipe start (the name is owned by something else) exits 1; `FirstPipeInstance` makes a squatter fail loudly. Until PR 2.8b the browser handler answers `hello` with `rules` that intercept nothing (`enabled: false`, empty lists) and everything else with `error INTERNAL`, so a browser falls back to its own download instead of waiting for an ack. A test holds the host build and checks that the pipe answers `hello` meanwhile.

**Hand-off** (plan decision 1, the recommended option): a second per-user pipe `kasi-<SID>-open`, one JSON line `{"args":[...]}` per launch, so protocol v1, the extension and the host stay untouched and the browser pipe keeps one purpose. The second process connects (waiting up to 5 s for a first instance that is still starting), checks that the pipe is served by a process of the same user, writes the line, waits for the pipe to drain and exits 0 whether or not that worked. The server accepts at most 64 KiB and 64 arguments, cuts a client off after 5 s and drops anything that is not the contract without an answer.

**Where it lives.** `HandOffServer` and `HandOffClient` are in `Kasi.Ipc`, not in `Kasi.App`. `PipeSecurityFactory` (current user allow, NETWORK deny, nothing else, owner set to the user) stays `internal`, so there is one place that decides pipe access, one test project that reads the access list back and creates a second Windows user to prove the denial (`CrossUserPipeTests` now covers both pipes), and `Kasi.App` cannot create a pipe with a different list by accident. The alternative, making the factory public, widens the library's surface for one caller.

**Arguments** (`AppArguments`): URLs to add and `--tray`, nothing else. A URL is taken only when it is absolute, http, https or ftp, at most `IpcLimits.UrlLength` (8,192) characters and free of control characters; everything else is counted, not kept. The same parser reads `Main`'s arguments and the hand-off. URLs may hold tokens: logs carry counts only.

**Delivery to the UI.** Requests go through `LaunchRelay`, which holds them until the UI attaches (they arrive before it exists) and then hands them to the UI thread. A request with URLs goes to an `IAddDownloadService` and raises the window; a bare `--tray` does nothing (the host sends it on every reconnect); a bare launch raises the window. The interface is what the Add dialog (PR 2.8d) will implement; until then `MainWindowViewModel` keeps the URLs in `PendingAddUrls`.

**`UiBatchDispatcher`** coalesces updates by key and applies the set once per period (250 ms in the app) through `PeriodicTimer(period, TimeProvider)`, so tests drive it with `FakeTimeProvider` and assert no wall-clock time. A failing apply is logged by exception type only and the loop continues.

**Test seam.** The environment variable `KASI_ENDPOINT_SUFFIX` appends to the lock and both pipe names so a test instance never meets a Kasi the developer has open. It cannot redirect the browser host (which uses the fixed names), so it adds no attack surface worth a build switch.

## Consequences

- A second launch costs one process start plus one pipe write; it never builds a host or a UI.
- With `--tray` and no tray icon yet (PR 2.8c), a hidden Kasi can be ended only from Task Manager; 2.8c adds the tray and Quit before this ships to users.
- The synchronous hand-off client and the pipe start keep `Main` on the STA thread without an `await`. The pipes are closed by process exit (tests dispose the `AppSession`).
- Still to decide in this ADR when the rest lands: what Quit does with browsers connected, toasts, the Add dialog's duplicate prompt and the picker (2.8c, 2.8d).
