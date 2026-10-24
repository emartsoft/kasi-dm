# UI redesign plan: the Fluent redesign of the main window, the active-transfer window and Settings

- **Phase:** M2, as **M2.14**, before the internal-alpha gate (question 1) ([`plan.md`](../../plan.md) section 5)
- **Requirements:** `KASI-UI-001`, `KASI-UI-003`, `KASI-UI-006`, `KASI-UI-007`, `KASI-UI-008`, `KASI-UI-010`, `KASI-NFR-006` ([TRD](../specs/KASI-TRD-2026-001.md) section 12); touches `KASI-UI-002` and `KASI-UI-004` for consistency only
- **Test cases:** `TC-UI-001`, `TC-UI-002`, `TC-UI-005`, `TC-UI-006`, `TC-UI-007` ([TPL](../specs/KASI-TPL-2026-001.md) section 5.9), all of which must stay green; the existing FlaUI flows are the regression net
- **Design source:** [`docs/wireframes/app_ui_redesign/`](../wireframes/app_ui_redesign/) (three screens, each a `screen.png` and the HTML it was drawn from), added by the owner:
  1. `kasi_download_manager_windows_11_fluent_redesign`: the main window;
  2. `kasi_download_manager_dedicated_download_dialog`: the "Active Transfer" window for one download;
  3. `kasi_download_manager_dedicated_settings_window`: Settings and Preferences.
- **ADR:** `docs/adr/NNNN-ui-design-system.md` (tokens, controls, icons, fonts; written with UIR-0)
- **Dependencies:** none new if questions 4 and 5 are answered as recommended (system fonts and the Segoe Fluent Icons font that ships with Windows 11; no chart library).
- **Delivery:** eight reviewable PRs, UIR-0 to UIR-7 (section 3). Each leaves the app working and the tests green; the old look is never half-replaced.
- **Status:** Approved by the owner on 2026-10-24, who accepted every recommendation in section 8 as written and asked that the items first left out of scope (section 7) take the same design language. **UIR-0 to UIR-5 and UIR-7 implemented** (tokens in both themes, the shared controls, ADR 0031, TRD 12 amended; the main window shell); UIR-6 is next (UIR-3 was split: 3a the window, 3b the per-job open-when-completed flag and speed limit, which touch the data layer and the engine). (UIR-2 put the check box inside the Name column rather than a column of its own, so the saved column layout is unchanged; there is no select-all box, Ctrl+A does it.)

---

## 1. What the design shows

### 1.1 The main window (screen 1)

A dark, card-based window on the Mica backdrop. In order of appearance:

- **Title bar:** the app mark, "Kasi", a version chip ("v3.4 Fluent"), a centred product name, then the caption buttons.
- **Toolbar:** Add URL (accent-filled) and the same eleven actions as TRD 12.2 as icon-and-label buttons, a speed-limit drop-down, a search box that shows its shortcut (Ctrl+F), and a settings button with a dot when the browser extension is connected.
- **Sidebar:** STATUS (All downloads, Active, Finished, Unfinished, each with a count pill), CATEGORIES (each with its own icon), QUEUES (a coloured dot, the queue name and its schedule window, for example "Night Queue (02:00 - 07:00)"). At the bottom a card with the **aggregate speed** (a number and a 60-second area chart, with down and up figures) and an **extension status** card ("Connected").
- **List:** a header line ("All Downloads", "6 items displayed"), a select-all check box, and columns FILE NAME, SIZE, PROGRESS. Each row has a check box, a **file-type chip** (ISO, MSI, EXE, ZIP), the file name over the host and path in a monospace face, the size, and a progress bar with the percentage and "3.72 GB / 5.80 GB". The bar's colour says the state (accent while downloading, green when done, amber for partial).
- **Settings docked on the right** as a panel (the Browser Integration page).
- **Status bar:** "2 active | 1 queued", Down and Up speed, **free disk space** of the download drive, and the extension state.

### 1.2 The Active Transfer window (screen 2)

A window for one download, replacing today's details window: a header card (type icon, file name, a state pill "Downloading (16 Streams Active)", destination with **Open Folder**, the URL with a copy button, and a **Pause** button); a large percentage with the current and peak speed and a gradient progress bar; four tiles (**Downloaded** of total, **Current speed** with the connection count and protocol, **Estimated time**, **Elapsed time**); a **60-second bandwidth chart**; a **segment matrix** with one tile per connection (number, percentage, speed, "Idle" when finished); and a footer with three check boxes (**Open file when completed**, **Shut down computer when completed**, **Scan with Windows Defender**) and a **Speed limit** list.

### 1.3 Settings (screen 3)

A full window with a navigation sidebar (General, Browser Integration, Connection & Speed, File Types & Paths, Scheduler & Queues, Advanced & Network, About & Updates), a "Find a setting" box (Ctrl+E), a breadcrumb and a page title, setting rows as cards with a title, a description and a toggle, an interception-status banner with a **Test Interception** button, the rules (extensions, MIME types, excluded sites) in cards with **Reset** links, and a footer with a sync message, **Cancel**, **Restore Defaults** and **Save Changes**. The sidebar also carries a profile card ("Local Machine Config"), **Open Config Directory** and **Export / Import Settings**.

## 2. What exists, what is new, what to drop

The screens are a design, not a spec: some of what they show is not in the TRD or not in Kasi yet, and a few things should not be built.

| Shown in the design | Today | Plan |
| --- | --- | --- |
| Toolbar actions, search, speed limit, settings | all there (M2.9) | restyle only (UIR-1) |
| Sidebar counts, categories, queues | there, as plain text | icons, count pills, queue schedule window from `queue_schedule` (UIR-1) |
| Aggregate speed chart in the sidebar | the status bar shows the total speed as text | new: a 60-sample history of the total speed, kept by the view model from the 250 ms tick (UIR-1) |
| Extension "Connected" card | text in the status bar | a card in the sidebar (UIR-1) |
| **Up** speed | none: Kasi only downloads (torrent upload is M4) | the sidebar card and the status bar are built with an **Up slot** that stays hidden until a torrent job is active (M4), so nothing is redrawn then; today they show Down only |
| **Free disk space** in the status bar | none | new: free space of the Downloads drive, refreshed every 5 s (UIR-1) |
| Row chip, host line, "size / total" | name, size, progress bar, speed, time left, status, date, category | templated columns inside the same `DataGrid`; the chip comes from the category rules' extension (UIR-2) |
| Select-all and row check boxes | multi-select by mouse and keyboard | a check-box column bound to the grid's own selection (UIR-2) |
| Right-hand Settings panel | a separate Settings window | **not adopted**: screen 3 is a dedicated window and is the one to build; the docked panel was an illustration (question 7) |
| Title-bar version chip "v3.4 Fluent" | none | **drop**; the real version goes on the About page when it exists |
| Segment map | one bar for the whole file plus a connection table (M2.9c) | the **matrix** replaces the table; the map stays as the header's progress (UIR-3) |
| 60-second bandwidth chart per job | none | new: a 60-sample ring of the job's speed from `JobProgressed` (once a second) (UIR-3) |
| Elapsed time | none | new: active time since this run of the transfer began, shown as such (question 8) |
| "Open file when completed" | the completion toast offers Open | new per-job flag, default off; the footer is one "When completed" row so the queue's post-action, the scan setting and this flag read as one family (UIR-3) |
| "Shut down computer when completed" | queue-level post-actions with a 60 s countdown (M2.10) | **not per job**: a switch that powers off the machine belongs to the queue; the control becomes a link to that queue's post-action (question 6) |
| "Scan with Windows Defender" | M2.11e (planned, off by default, a global setting) | shows the global setting's state and links to it; no per-job override (question 6) |
| Per-job speed limit | `job.speed_limit_bps` and the engine support it; no UI | a list of presets, as the tray has (UIR-3) |
| Settings navigation, breadcrumb, cards, toggles | two tabs plus the Categories tab (M2.11c) | the navigation shell and the pages that have content: General, Browser Integration, File Types & Paths (the Categories page), Scheduler & Queues (opens today's window until it is folded in). Pages with nothing to show yet (Connection & Speed, Advanced & Network, About & Updates) are not shown: no empty pages (UIR-4) |
| "Find a setting" | none | a filter over the labels of the pages that exist (UIR-4) |
| Profile card "Local Machine Config" | none | Kasi has no profiles, so the card becomes a **"This PC" card** of real facts in the same style: the data folder (shortened), the app version, the extension state (UIR-4) |
| Test Interception | none | the banner is built now and shows the **live** connection state (browsers connected, from the pipe); its Test button arrives with a self-test message in the protocol, as a later change (question 9) |
| Open Config Directory | none | new, trivial: opens the data folder (UIR-4) |
| Export / Import Settings | `KASI-SYS-004`, P2, not started | the sidebar footer row is designed now (section 7) and appears when SYS-004 lands |
| Reset links, Restore Defaults | none | new, small (UIR-4) |
| Fonts: Segoe UI, JetBrains Mono | the app uses the system font | Segoe UI Variable for text and **Consolas** for the monospace parts: both ship with Windows, so no font to bundle (question 4) |
| Icons | text only | glyphs of the Segoe Fluent Icons font (Windows 11) with Segoe MDL2 Assets as the Windows 10 fallback (question 5) |
| Colours | FluentAvalonia's theme with the system accent | the design's tokens: surfaces `#0f0f13` base, `#16161c` mica, `#1e1e26` card, `#2c2d3a` border, accent indigo `#6366f1` (question 3) |

## 3. Delivery

| PR | What it delivers | Closes |
| --- | --- | --- |
| **UIR-0** design system | tokens as theme resources (dark first, light derived: question 2), card, status chip, file-type chip, toggle row, sparkline control, the icon helper, the font choices; ADR; TRD section 12 amended to point at the new design (the TRD says the main window "matches the approved design in section 12", so the design of record changes here, with the owner's approval) | nothing visible alone |
| **UIR-1** main window shell | custom title bar, toolbar, sidebar (icons, counts, queues with schedule, aggregate-speed card, extension card), status bar with free disk space | `UI-001` look |
| **UIR-2** the list | templated columns in the existing `DataGrid`: check box, chip, name and host, size, progress with sizes, status badge; the layout persistence (UI-007) and sort keep working | `UI-001`, `UI-007` |
| **UIR-3** Active Transfer window | replaces the details window: header, tiles, chart, matrix, footer; the per-job "open when completed" flag and speed limit | `UI-003` look |
| **UIR-4** Settings window | the navigation shell, the breadcrumb, cards and toggles, the "find a setting" filter, Reset and Restore Defaults, Open Config Directory; General, Browser Integration, File Types & Paths and Scheduler & Queues pages | `UI-006` for Settings |
| **UIR-5** the other windows | Add download, link picker, refresh link, Scheduler and the confirmation dialogs take the same tokens and controls, so nothing looks left behind (the design has no mock for these, so they follow the system, not new designs) | consistency |
| **UIR-7** tray flyout and toasts (section 7) | a styled flyout for the tray icon in place of the native menu, and toast content in the same language | `UI-004`, `UI-005` look |
| **UIR-6** light theme and accessibility | the light variant, a high-contrast check, a keyboard and Narrator pass over every window, the FlaUI flows updated for any name that changed | `UI-006`, `UI-010` |

UIR-7 needs UIR-0 and the main window's sidebar card (UIR-1). UIR-1 and UIR-3 are independent; UIR-2 follows UIR-1; UIR-4 needs only UIR-0. They can be built in parallel by separate sessions once UIR-0 has merged (they touch different views).

## 4. Decisions

1. **The design is followed where it describes something Kasi does, and trimmed where it does not.** Section 2 is the list. Nothing is built just because it is drawn: no profile card, no version chip, no upload figure, no empty settings pages, no per-job shutdown.
2. **Same stack.** Avalonia with FluentAvalonia and `DataGrid`; the redesign is styles, templates and a few small custom controls (the chart, the segment matrix). No TreeDataGrid, no chart package.
3. **Automation names do not change** unless a control's meaning does; every new control gets a name from a resource string (UI-010). The FlaUI flows and the headless name checks are the net, and a rename updates them in the same PR.
4. **Performance budgets stay.** The list still filters 10,000 rows under 100 ms and updates 20 active jobs under 16 ms a frame (`TC-UI-006`, `NFR-006`); the templated cells are plain text blocks and one drawn bar, no per-row animation, and the existing budget tests run on every UIR PR. The design's animated stripes and pulse are applied to **one** element at a time (the selected row's bar), never to the whole list.
5. **Charts are drawn controls** (`Control.Render`), like the segment map: a ring buffer and a polyline with a gradient fill, one control, no allocation per frame.
6. **Mica stays** as the window backdrop (M2.9d); the cards sit on it as solid surfaces. The design's "acrylic blur" CSS is not reproduced.
7. **Text is in resource files** (UI-009), and a card's layout must survive a text 40% longer (Kiswahili).
8. **Screenshots of every redesigned window** go in each PR (taken from the running app, one window at a time), next to the mock they follow.

## 5. Tests

- Every PR: the existing suites (view models, headless windows, FlaUI smoke and nightly flows, the 10,000-row budgets) stay green.
- UIR-1: view-model tests for the speed history (60 samples, oldest dropped), free disk space (injected provider), queue schedule text.
- UIR-2: tests that the check-box column and the grid selection are one thing; the sort and column layout still persist.
- UIR-3: the ring buffer and the matrix (one tile per connection, "Idle" when a connection has finished), the per-job flag and speed limit reach the engine, the update rate still at least twice a second (`TC-UI-002`).
- UIR-4: the filter finds a setting by its label, Reset and Restore Defaults restore the defaults, every page's controls are named; a FlaUI flow through the navigation.
- UIR-6: light and high-contrast checks, and a Narrator-name listing diffed against the previous names.

## 6. Risks

| Risk | Handling |
| --- | --- |
| A templated `DataGrid` is slower than text cells | measured in UIR-2 against the 10,000-row budget before the template is accepted; the fall-back is the plain cells with the chip only |
| The mocks are HTML and CSS, which Avalonia does not render the same way (blur, backdrop filters, some shadows) | Mica plus solid surfaces; the design's look is matched by colour, radius and spacing, not by effect |
| A big-bang restyle breaks tests and review | seven PRs, each green; the old look is replaced window by window |
| The design drifts from the TRD | UIR-0 amends TRD 12 and the wireframe reference in the same PR, with the owner's sign-off |
| Dark-only mocks and a light theme requirement (UI-006) | questions 2 and 3; light is derived and checked in UIR-6 |

## 7. Beyond the three screens, in the same language

The design covers three windows. Everything else that a user sees, or will see, takes the same tokens, controls, icons and spacing, so nothing looks like it came from another app. What each one becomes, and when:

| Item | Design, in the language of the three screens | When |
| --- | --- | --- |
| **Tray icon menu** (`UI-004`) | a **flyout** window instead of the native menu: a header card with the total speed and a small sparkline, Pause all and Resume all as buttons, the speed-limit presets as a list, Open Kasi and Quit at the foot; keyboard-operable, with automation names, closing on Esc or when it loses focus. The native menu stays as the fallback if the flyout cannot be shown | UIR-7 |
| **Toast notifications** (`UI-005`) | Windows draws toasts, so only their content follows the design: the Kasi mark as the app logo, the file-type glyph and name, the size and the time taken in the body, Open and Open folder as the buttons, and a failure toast that names the reason in plain words | UIR-7 |
| **About page** | a Settings page in the same card style: the Kasi mark, the version, the build date, the licence and a list of third-party notices; the update line stays out until updates exist | UIR-4 (version and notices); updates with M4 |
| **Installer and first run** (M4) | a welcome window and the installer's own pages use the same tokens (indigo accent, card surfaces, Segoe UI Variable), so the first thing a person sees is the app's look | with the M4 installer |
| **Upload speed** (torrents, M4) | the Up slot of the sidebar card and the status bar (section 2), hidden until a torrent job is active; a torrent row gets the same chip, bar and badge as any download | M4 |
| **Profiles** | not built; the "This PC" card (section 2) takes the place the design gave the profile card | UIR-4 |
| **Per-job power actions** | not built; the "When completed" row in the Active Transfer footer shows the queue's post-action and links to it, in the same chip style | UIR-3 |
| **Settings export and import** (`KASI-SYS-004`) | a footer row of the Settings navigation, "Export / Import settings" with a JSON chip as in screen 3, opening a small dialog in the same style | when SYS-004 is built |
| **Test Interception** | the banner's button, with the result shown on the banner (green tick or the reason), as in screen 3 | with the protocol self-test |
| **Media capture UI** (M3: the stream picker, the DRM refusal message, the quota and wake settings) | built in this design from the start; the M3 plan's windows use these tokens and controls, and the refusal text sits in a card with the same status chip | with each M3 PR |

Not redesigned at all: the Windows shell's own surfaces (file dialogs, the SmartScreen prompt, the UAC prompt) and the browser extension's popup, which follows the browser's look.

## 8. Questions for the owner (all answered 2026-10-24: every recommendation accepted)

1. **Where does this sit?** As M2.14, before the internal-alpha gate, or after it as its own milestone? (Recommended: before the gate, since the alpha is the first thing people see; UIR-0 to UIR-4 are what matters, UIR-5, UIR-6 and UIR-7 can follow the alpha.)
2. **Light theme.** The mocks are dark only. Derive a light theme from the same tokens (recommended), or ship dark only for now and keep following the system for the rest?
3. **Accent.** Use the design's fixed indigo (`#6366f1`) as Kasi's accent in both themes (recommended: it is the brand), or follow the Windows accent colour?
4. **Fonts.** Segoe UI Variable and Consolas, which ship with Windows (recommended), or bundle JetBrains Mono as in the mocks (SIL Open Font Licence, a font file in the installer)?
5. **Icons.** The Segoe Fluent Icons font that ships with Windows 11, with Segoe MDL2 Assets on Windows 10 (recommended), or a bundled icon set (a new dependency; state its licence first)?
6. **Per-job footer options.** "Open file when completed" as a per-job flag (recommended). "Shut down computer when completed" as a link to the queue's post-action, not a per-job switch (recommended). "Scan with Defender" shows the global setting from M2.11e and links to it (recommended). Agree?
7. **Settings as a window.** Build screen 3 (a dedicated window) and not the docked right panel of screen 1 (recommended)?
8. **Elapsed time.** Time since this run of the transfer began, labelled as such (recommended), or stored as a total across pauses (a new column and migration)?
9. **Test Interception.** Leave it for a later change that adds a self-test message to the protocol (recommended), or include it now?
10. **UIR-5.** Restyle the other windows to match, with no new designs (recommended), or wait for mocks of them?
11. **Wireframes.** The PDF of wireframes (`KASI-UIW-2026-001`) is the design of record today. Keep it as history and make the three redesign screens the record, noting that in TRD section 12 (recommended)?
