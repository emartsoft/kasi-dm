# ADR 0026: UI Automation tests against the real Kasi window

- **Status:** Proposed (M2.13a to 13c)
- **Requirements:** KASI-UI-001, KASI-UI-003, KASI-UI-004, KASI-BRW-002 | **Test cases:** TC-UI-001
- **Plan:** [`docs/plans/m2-13.md`](../plans/m2-13.md)

## Context

The alpha gate asks for a FlaUI smoke suite (TPL section 4). `Kasi.App.Tests` drives views headlessly with Avalonia.Headless; nothing yet starts the built `Kasi.App.exe` and works its window the way a user does.

## Decision

1. **A separate project, `tests/Kasi.App.UiTests`,** using FlaUI (MIT) over UI Automation (UIA3). It starts `Kasi.App.exe` from a published copy of the app (`app/` beside the test assembly, made by a build step) as a child process, so the real start-up path and the single-instance lock are used. The copy matters: the test project takes the ASP.NET shared framework in through Kasi.TestServer, and a plain build then leaves out the Microsoft.Extensions.* assemblies the app needs (the first CI run failed on that). It runs only when `KASI_UI=1`; otherwise its tests are skipped with that reason, so `dotnet test` on a developer machine and the main CI job are unchanged.
2. **Isolation per test:** a temp data folder (`KASI_DATA_DIR`), a unique endpoint suffix (`KASI_ENDPOINT_SUFFIX`), and an in-process `Kasi.TestServer` on loopback. Disposing the app ends the process tree and fails if it does not end within 15 s.
3. **UI Automation patterns, not input,** wherever the app exposes one: text boxes are set through the Value pattern, buttons through Invoke. Controls are found by control type and name (a label above a text box carries the same name as the box). The window title is the name of the main window (`Kasi`); the automation name `Kasi main window` is not what UIA reports for a window.
4. **One exception:** Avalonia's `DataGrid` rows expose only the legacy accessibility and scroll-into-view patterns, not selection, so selecting a row takes a real mouse click. Tests that need it use `[UiInputFact]` and run only with `KASI_UI_INPUT=1`; a session that cannot inject input (a locked or remote desktop) skips them with that reason. Making the rows selectable through UI Automation is an accessibility improvement worth its own change (TC-UI-007).
5. **CI:** `ui-smoke.yml` runs the suite on pull requests that touch the app, its engine or these tests, on `windows-latest`. The nightly with the full flow set comes in M2.13b.
6. **A failure explains itself:** a wait that times out throws with a dump of the control tree it was looking at.

## M2.13b: the other flows

- **Windows and flows added:** the Scheduler window (tabs, Cancel), the link picker started by a real `addBatch` on the app's pipe (filter, Select shown, Add selected; only the ticked link downloads, SHA-256 checked), closing the window leaves Kasi running and a second launch brings the window back (the single-instance hand-off), Delete with its confirmation and the files option, and the details window opened by a double-click. The last two click the grid and so run only with `KASI_UI_INPUT=1`.
- **`BrowserPlayer`** is the extension's end of the pipe in the tests: hello, then `addBatch`. It uses `Kasi.Ipc` as the host does and needs no new app code.
- **Tests never run side by side** (`DisableTestParallelization`): they share the desktop's focus and mouse and one UI Automation client, and a run with two apps at once was flaky locally.
- **Left for 13c:** the refresh-link window, which needs the extension to answer `refreshLink`.
- **Not driven:** the tray icon's menu (Pause all, Quit). UI Automation does not reach the shell's tray menu reliably, so the tray is covered by the closing-the-window flow above, the headless tray tests, and the manual list for the M4 installer.
- **Nightly:** `nightly-ui.yml` runs the whole project at 03:15 UTC with the mouse tests on.

## M2.13c: the real Kasi behind the browser

- **Same browser, same native host, real far end.** `extension/tests/e2e/real-scenarios.ts` publishes `Kasi.App` beside the host as `Kasi.exe` (the apphost finds its dll next to itself) and lets the host start it with `--tray`, as it does for a user. The harness serves only the test pages and `Kasi.TestServer` (`stub: false`); no stub owns the pipe.
- **Isolation by environment.** The browser starts the host and the host starts Kasi, and each inherits the test process's environment: `KASI_DATA_DIR` (data, settings), `KASI_DOWNLOADS_DIR` (what is saved) and `KASI_ENDPOINT_SUFFIX` (pipes and instance lock). Three small seams made that possible: `KasiPipeNames` appends the suffix for the host as the app already did, `AppPaths.DownloadsFolder` honours `KASI_DOWNLOADS_DIR`, and the browser settings file moved into the data folder, so `KASI_DATA_DIR` moves it with the rest. A Kasi the developer runs is never reached, and the user's Downloads is never written.
- **No one to press Add.** The real app asks before adding what a browser hands over; the test seeds `showAddDialog: false` in its own settings file.
- **Cases:** an intercepted download is saved by the real Kasi (SHA-256 of the file) and the browser keeps no copy (TC-BRW-001); a file behind the test server's cookie gate downloads with the browser's cookie, and no file in Kasi's data folder contains the cookie name (M2.12).
- **Where it runs:** nightly and by hand in `extension-e2e.yml`, Chromium and Firefox, after the stub run. Pull requests keep the stub run only, so their time does not grow.
- **Not covered:** the refresh-link window against a real extension (the extension half is tested against the stub, the Kasi half headlessly and in the picker/refresh view-model tests), and the link picker against a real browser (the picker is driven by a real `addBatch` in M2.13b). The real Kasi is ended by the path of its executable, never by image name.

## Consequences

- The three first flows (window opens with its toolbar, sidebar, list and status bar; Add dialog to a finished download checked by SHA-256 and shown in the list; Pause then Resume) run against the real window.
- The tray menu, the tray icon and toasts are not covered here (13b and the manual M4 list).
- The Pause and Resume test could not be run in the session that wrote it (that session could not inject mouse input); the first run on the CI desktop is its proof.
- **First finding:** the first CI run of the Pause and Resume test showed that a toolbar button stayed disabled after the selected download paused (the buttons were refreshed when the selection changed, not when a selected row changed state). `MainWindowViewModel` now follows the selected rows; a headless test covers it.
- **Second finding:** a Resume pressed the moment a download showed Paused could do nothing: the row says Paused one step before the runner that paused it is removed, and the resume failed with "already running", which the toolbar swallows. `DownloadEngine.ResumeJobAsync` now waits for that runner to finish; an engine test holds the pause at its last step to cover it (TC-ENG-015).
