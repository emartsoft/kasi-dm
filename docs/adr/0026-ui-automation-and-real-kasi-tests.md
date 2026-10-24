# ADR 0026: UI Automation tests against the real Kasi window

- **Status:** Proposed (M2.13a; 13b and 13c extend it)
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

## Consequences

- The three first flows (window opens with its toolbar, sidebar, list and status bar; Add dialog to a finished download checked by SHA-256 and shown in the list; Pause then Resume) run against the real window.
- The tray menu, the tray icon and toasts are not covered here (13b and the manual M4 list).
- The Pause and Resume test could not be run in the session that wrote it (that session could not inject mouse input); the first run on the CI desktop is its proof.
