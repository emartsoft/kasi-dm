# CLAUDE.md — Kasi Download Manager

Kasi is a Windows download manager (owner: MartSoft Solutions) built on .NET 10 and Avalonia. It accelerates downloads with dynamic segmentation, resumes from the exact saved byte after any interruption, captures unencrypted HLS/DASH streams, and runs HTTP, FTP, media and BitTorrent jobs in one queue.

## Source of truth

Read these before planning or writing code. When they conflict, the TRD wins for *what* to build; CONTRIBUTING.md wins for *how*.

| Document | Path | Use it for |
| --- | --- | --- |
| Technical Requirements & Architecture (TRD) | `docs/specs/KASI-TRD-2026-001.md` (diagrams in the `.pdf` beside it) | Requirements (KASI-ENG-001 …), architecture, engine design, data schema, roadmap |
| Test plan | `docs/specs/KASI-TPL-2026-001.md` | Test levels, test case IDs (TC-ENG-001 …), gate criteria, test server design |
| UI wireframes | `docs/wireframes/` | Layout for main window, job details, add dialog, media picker |
| Working agreement | `CONTRIBUTING.md` | Branching, commits, code style, PR rules, definition of done |

Always cite requirement IDs (KASI-…) and test case IDs (TC-…) in plans, commit bodies and test traits.

## Current phase

**M4 — packaging, portable builds, signed updates and the public beta (TRD section 16; plan.md section 7).** 4a (speed profiles and scheduled bandwidth) is merged; M4.5 to M4.12 follow. M3 media and scheduler work is merged, with the wake-from-sleep hardware evidence and the M1 engine gate still open. The M2 store submissions remain owner steps. Update this line when the phase changes.

## Stack (do not substitute without asking)

- .NET 10 LTS, C# latest, nullable enabled, `TreatWarningsAsErrors`
- UI: Avalonia (latest stable) + FluentAvalonia + CommunityToolkit.Mvvm; job list uses `Avalonia.Controls.DataGrid` (MIT)
- **Not allowed:** DevExpress, Avalonia Accelerate components (including the current TreeDataGrid), WPF, any GPL dependency
- HTTP: `SocketsHttpHandler`; FTP: FluentFTP; torrents: MonoTorrent; storage: SQLite (WAL) via Microsoft.Data.Sqlite + Dapper
- Muxing: FFmpeg LGPL build as an external process; installer/updates: Velopack; logging: Serilog
- Extension: TypeScript, Manifest V3, Vite, webextension-polyfill; native host ID `com.martsoft.kasi`
- Tests: xUnit, AwesomeAssertions (the maintained FluentAssertions fork, Apache-2.0), FsCheck, Testcontainers, Avalonia.Headless, FlaUI, Vitest, Playwright

## Solution layout

```
src/   Kasi.Core, Kasi.Engine, Kasi.Media, Kasi.Torrent, Kasi.Scheduling,
       Kasi.Data, Kasi.Ipc, Kasi.App (Avalonia), Kasi.NativeHost (Native AOT)
extension/   TypeScript MV3 extension
tests/ Kasi.*.Tests, Kasi.TestServer, Kasi.FaultInjection, Kasi.ArchitectureTests, fixtures/
bench/ build/ docs/
```

## Hard rules

1. **Engine has no UI dependency.** Nothing in Core, Engine, Media, Torrent, Scheduling, Data or Ipc references Avalonia (KASI-NFR-019). `Kasi.ArchitectureTests` enforces it.
2. **Never lose or corrupt data.** Bytes are recorded as written only after `FlushFileBuffers`; resume checks `If-Range` against ETag/Last-Modified (TRD 6.5).
3. **No sensitive data in logs, telemetry or crash reports**: no URLs with query strings, cookies, headers, tokens or full user paths. Use the redaction helpers.
4. **DRM refusal is not configurable.** Never add a setting, flag or code path that bypasses DRM detection (TRD 7.3).
5. **Tests never reach the public internet.** Use `Kasi.TestServer`, chaos profiles or Testcontainers. Download tests verify SHA-256.
6. **Async all the way.** No `.Result`, `.Wait()` or `GetAwaiter().GetResult()`. Library projects use `ConfigureAwait(false)`. Use `TimeProvider`, not `DateTime.Now`.
7. **Hot paths don't allocate per chunk.** Use `ArrayPool<byte>`, `Memory<T>`, `Span<T>`.
8. Ask before adding any dependency not listed above; state its licence.

## How to work in this repo

- For any task larger than a small fix: read the relevant TRD and test plan sections, then propose a short plan naming the requirement and TC IDs it covers. Wait for approval before writing code.
- Work in small, reviewable steps. Each step builds and passes tests before the next starts.
- For defect fixes, write the failing test first.
- Tag tests: `[Trait("TC", "TC-ENG-008")]`; integration tests also `[Trait("Category", "Integration")]`.
- Follow Conventional Commits with scopes from CONTRIBUTING.md (`engine`, `media`, `data`, `app`, …). Do not push or open PRs unless asked.
- Record significant design decisions as ADRs in `docs/adr/`.
- Keep `README.md` (status, layout, commands) and `plan.md` (task status, "Last updated") current: update them in the same PR as the work that changes them, and say so in the PR description.

## Commands

```powershell
dotnet restore
dotnet build -c Debug
dotnet test                                    # needs Docker for Testcontainers
dotnet test --filter "Category!=Integration"   # fast unit tests
dotnet test --filter "TC=TC-ENG-008"           # one test-plan case
dotnet format --verify-no-changes
dotnet run --project src/Kasi.App
cd extension; npm ci; npm test
```

## Definition of done

See CONTRIBUTING.md section 6. In short: acceptance criteria pass with tagged tests, CI green, coverage not reduced (engine ≥ 80%), no sensitive data in logs, UI keyboard-accessible with automation names, user-visible text in resource files.
