# ADR 0024: Main window and job views

- Status: Accepted (M2.9a to 9d implemented)
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

**Layout, sort and theme** (M2.9b, KASI-UI-006, KASI-UI-007). `ui-settings.json` (next to the database, written through a temporary file, defaults on a missing or unreadable file, an older file without the new fields loads with defaults) now also holds the theme override, the column layout and the sort. `ColumnLayoutViewModel` hands the grid the saved layout over the defaults and checks everything it hands out: an unknown column or a duplicate in the file is ignored, a width outside 40 to 1200 pixels falls back to the default, display places are made contiguous, and the name column can never be hidden (it is how a row is recognised and read out); a sort on an unknown column is ignored. The name column's width is never saved, so it keeps taking the room that is left. The window applies the layout when it opens and saves it when the user reorders, sorts or hides a column from the grid's context menu ("Columns", with "Reset columns") and when it closes, so widths dragged since the last event are kept too.

**The grid's sort survives the list changing.** The first version replaced the list object whenever a filter or a state change altered what is shown, and a DataGrid forgets its sort with its list. `Visible` is now one `BulkObservableCollection` for the life of the window: a large change is one reset, a handful of changes are single adds and removes (so a selection survives a row leaving the Active view), and an unchanged result raises nothing.

**Theme.** The Settings window's General tab has a Theme list: Follow Windows (the default; Avalonia's default variant, which follows the setting live), Light and Dark. Saving writes the override; the app applies the saved theme at start and again whenever it changes, and the preference is stored by name. `TC-UI-005`: the default follows Windows, the override persists across a restart.

**Visual check.** Run for real with a seeded layout and `"theme": "Light"`: the Category column came first, Speed and Time left were hidden, the grid was sorted by Size, largest first, with the sort arrow, and the window was light.

**Details window and the engine events behind it** (M2.9c, KASI-UI-003). Two events in `Kasi.Core`, raised by the engine on the scheduler's own pulse (never per chunk): `SegmentsProgressed` (every segment with the bytes received so far, and each live connection with its range and speed) at most four times a second while a job transfers, and `JobLogged` (a short line built from numbers and fixed words: the probe result, a connection's retry, a split, the connection count; never an address, a header or a path, and a test checks that). The per-connection speed is the lease cursor's movement over the time since the last snapshot. The scheduler grew a `GetLeaseSnapshot()` for the connections, and the runner an internal `Log` option for the probe and retry lines.

`DetailsViewModel` follows those events for one job and applies every snapshot at once, so the segment map moves at least as often as the engine publishes (four a second, checked in an engine test against a throttled server, and in the view model by counting the changes it makes). It shows the file, the address **without its query or fragment** (a query can carry a token, and the window can be screenshotted), where it is saved, the numbers the list row already has, a connection table, and the log tail (newest first, the last hundred, state changes included). `SegmentMapControl` draws the map itself, one rectangle per segment as wide as the segment is long, so thousands of segments cost one render. Refresh link, Open folder, Pause or Resume and Close are buttons; one details window per download, and asking again brings the open one forward. Double-click, Enter or the context menu's "Details..." opens it for a running download.

**Visual check.** The window was run for real with a seeded 12-segment, 8-connection snapshot (captured by asking only that window to paint itself): the map, the table and the log drew correctly, and the missing table headings found that way were added.

**Performance budget, accessibility and Mica** (M2.9d, KASI-UI-006, KASI-UI-010, KASI-NFR-006, TC-UI-006, TC-UI-007). `BulkObservableCollection<T>.ReplaceAll` fast-paths unchanged content via an indexed loop and avoids `HashSet` allocations when the count delta exceeds 64 items, enabling 10,000-job filtering under 100 ms. Applying progress updates for 20 active jobs on the 250 ms tick takes well under 16 ms (1 frame at 60 fps). All controls across the window, context menus, and cell progress bars declare explicit `AutomationProperties.Name` for screen readers (Narrator). Pressing `Escape` while focused on the search box clears the query and returns keyboard focus directly to the downloads grid. The window enables Mica on Windows 11 via `TransparencyLevelHint="Mica, AcrylicBlur, None"` with `SolidBackgroundFillColorBaseBrush` fallback, dynamically toggling background transparency when Mica is composited.


## Consequences

- Filtering and counting are O(n) per change. Measured at 10,000 rows: about 10 ms to filter (the spec asks for under 100 ms) and under 1 ms to apply 20 active job updates (a frame is 16 ms). The tests assert much looser bounds (2 s, and one 250 ms tick) on purpose, because a stalled shared CI runner can exceed a tight stopwatch limit; they still fail on quadratic work or on cost that grows with the list.
- The delete confirmation setting lives in a new small JSON file; 9b adds the column layout and theme to it.
- Mica backdrop provides native Windows 11 styling with graceful fallback to theme brushes on Windows 10/Linux/headless.
