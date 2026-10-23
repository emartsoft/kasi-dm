# ADR 0024: Main window and job views

- Status: Proposed (M2.9a; 9b to 9d extend it)
- Requirements: KASI-UI-001, KASI-UI-008, KASI-UI-010, KASI-NFR-006 (groundwork)
- Plan: `docs/plans/m2-9.md`

## Context

After M2.8 the main window was a four-column list. The wireframes and TRD 12.2 call for a toolbar, a status and category sidebar, queue entries, eight columns, a search box and a status bar, and the list must stay responsive with 10,000 rows.

## Decision

**View models only decide; the window draws.** `JobListViewModel` keeps every job (`All`) and the filtered list (`Visible`). `Visible` is replaced as a whole when the filter or the membership changes, so 10,000 rows cost one list rebuild, not 10,000 collection events; a row that only progresses raises its own property changes through the existing 250 ms `UiBatchDispatcher`. A `JobFilter` combines the sidebar's status, category or queue with the search text (matched against the file name, any case); a state change re-filters, so a row leaves "Active" when it pauses. Counts (all, active, finished, unfinished, queued) are recomputed on membership and state changes, never per byte. The total speed is the sum of the moving jobs and is applied once per tick.

**Rows.** `JobRowViewModel` shows the file name, never the address, and exposes numbers to sort by (`SortSize`, `SortSpeed`, `SortTimeLeft`, creation time) so "Size" sorts by bytes and an unknown time sorts last. Speed is a 0.7/0.3 moving average so the number does not jitter and "time left" does not leap. Words and formats (status labels, "4 min", "Today 09:12") live in `JobText` and `Strings.resx`.

**Sidebar.** `SidebarViewModel` is one flat list of headings and entries; one entry is selected, a heading cannot be (the selection snaps back), and the choice drives `JobListViewModel.SetFilter`.

**Commands.** `MainWindowViewModel` acts on the grid's selection through `IJobService` only: Resume starts queued and resumes paused jobs, Pause and Stop pause running ones (Stop equals Pause until the scheduler exists), Delete and Delete completed ask first through `IConfirmHost` (the delete window offers "also delete the files" and "do not ask again", which is saved in `ui-settings.json` next to the database; Delete completed never deletes files, whatever the window answers), Space toggles, Enter and double-click open a completed file or ask for the details window. The three queue buttons exist and are disabled (they come alive with M2.10). The speed limit is one `SpeedLimitState` shared by the tray, the toolbar and the status bar, so changing it anywhere updates the others.

**Status bar.** Total speed, counts, the limit, and "browser extension connected", read from the pipe's connection count once a second (the one thing with no event).

**Visual check.** The window was run for real against a seeded database and photographed. Two defects only a real run shows were fixed: FluentAvalonia's theme leaves the DataGrid's header text black on the dark window (a style sets it), and the Fluent `ProgressBar` has a 200 px minimum width that pushed the percent under the bar.

## Consequences

- Filtering and counting are O(n) per change, measured as fast at 10,000 rows; the test uses a generous bound and the spec's 100 ms is checked in 9d with a real budget.
- The delete confirmation setting lives in a new small JSON file; 9b adds the column layout and theme to it.
- Mica, the details window, saved layout and the theme override are the later PRs of M2.9.
