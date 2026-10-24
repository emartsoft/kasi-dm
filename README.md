# Kasi Download Manager

[![CI](https://github.com/emartsoft/kasi-dm/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/emartsoft/kasi-dm/actions/workflows/ci.yml)
![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![Avalonia 12](https://img.shields.io/badge/Avalonia-12.1-8B44AC)
![TypeScript 5.9](https://img.shields.io/badge/TypeScript-5.9-3178C6?logo=typescript&logoColor=white)
![SQLite WAL](https://img.shields.io/badge/SQLite-WAL-003B57?logo=sqlite&logoColor=white)
![Windows 10 22H2+ and 11](https://img.shields.io/badge/Windows-10%2022H2%2B%20%7C%2011-0078D4)
[![MIT licence](https://img.shields.io/badge/licence-MIT-green)](LICENSE)

Kasi is a Windows download manager built on .NET 10 and Avalonia. It accelerates downloads with dynamic segmentation, resumes from the exact saved byte after any interruption, captures unencrypted HLS/DASH streams, and runs HTTP, FTP, media and BitTorrent jobs in one queue with one set of bandwidth limits.

| | |
| --- | --- |
| Owner | MartSoft Solutions |
| Specification | `KASI-TRD-2026-001` (Technical Requirements & Architecture Specification) |
| Test plan | `KASI-TPL-2026-001` |
| UI wireframes | `KASI-UIW-2026-001` |
| Platforms | Windows 10 22H2+, Windows 11 (x64, ARM64) |
| Browsers | Chrome, Edge, Firefox (latest 3) and Firefox ESR |

## Status

Under construction in **M3 (media capture)**; see [plan.md](plan.md) for task status. The M1 download engine supports segmented HTTP and FTP/FTPS downloads, durable resume, authentication, proxies, checksums, duplicate detection and shared bandwidth limits. The engine gate still needs the owner's benchmark and fault-injection evidence. M3.1 adds the HLS and DASH parsers, the neutral media stream model, quality estimates/pairing, manifest caps and the HLS/MPD fuzz targets. M3.2 adds the bounded `MediaSourceLoader` and the single DRM gate: every protected HLS/DASH kind is refused before a segment request, and only the loader can produce a playable plan. M3.3 adds the media job tables and `MediaJobRunner`: whole segments download in parallel through the engine pool, are written `.part` → flushed → renamed → batched into SQLite, decrypted when AES-128, and joined init-first for muxing. M3.4 adds `MediaMuxer` and `FfmpegRunner`: an external LGPL FFmpeg process with a kill-on-close job object, MP4/MKV/audio-only stream copy, redacted stderr and an `ffprobe` duration check. The 3e glue adds the optional `mediaDetected.thumbnailUrl`/`contentType` and `captureMedia.origin` fields, defined replies, a per-tab detection catalogue, the capture path (refuse DRM or create the default media job, direct media through the Add path) and a routing `IJobControl` that sends media jobs to the media runner. M3.6 adds the media picker: title and thumbnail, every quality with codec/bitrate/size, the default at or below the preferred maximum, MP4/MKV/audio-only, disabled subtitle rows and keyboard-accessible Download/Cancel. The capture flow now shows it and carries the chosen variant and format into the media job.

M2.1 (merged) adds strict IPC message contracts, the JSON Schema, shared protocol fixtures and the hardened per-user named pipe. M2.3a adds the TypeScript extension scaffold with separate Chromium and Firefox MV3 bundles and schema-generated types; M2.3b adds its native port manager, rules cache, redacting logger, Ajv fixture tests, CI job and nightly browser smoke. M2.2 adds the native messaging host (browser frames, relay to the pipe, launcher, developer registration script), fuzzed with 100,000 malformed messages in the tests and nightly with SharpFuzz, and published with Native AOT in CI (size limit and start-up time recorded). M2.4a adds the rule semantics (extension, MIME type, excluded sites, minimum size, bypass key) as pure, tested extension code and the optional `minSizeBytes` on `rules`. M2.4b adds the interception logic to the extension (pause, cookies, hand-off to Kasi with a 2 s acknowledgement, bypass key, exclusions, disconnected badge), tested with a fake browser API. M2.4c adds the end-to-end harness: `tests/Kasi.BrowserHarness` (a stub Kasi on the real pipe, test pages) with the real Native AOT host, driven from Vitest in Playwright's Chromium and, through geckodriver, Firefox (`npm run test:e2e` in `extension/`; Windows only, opt-in with `KASI_E2E=1`; Chromium on path-filtered pull requests, both browsers nightly). M2.8a adds the desktop shell's start-up: the browser pipe opens before the engine and UI, a per-user instance lock, and a second launch hands its URLs and `--tray` to the running instance over a second per-user pipe (`kasi-<SID>-open`) and exits 0 (ADR 0023). M2.5a adds the connection status that turns red when Kasi quits (the host reports `APP_NOT_RUNNING` and exits instead of relaunching Kasi; the extension reconnects only on a download, the popup or a browser start) and the popup's interception toggle. M2.8b adds Kasi's side of the browser protocol: rules from a settings file (populated defaults), an ack for `addDownload` and `addBatch` once the request is queued, `openSettings` and a Settings window skeleton. M2.6 puts the real store IDs in `extension/store-ids.json` (the Firefox add-on ID is now `kasi@martsoft.co.ke`), lets the registration script allow several IDs per browser under a test registry root, and proves in Chromium and Firefox that an extension outside the allow-list cannot reach the host (`TC-BRW-007`). M2.5b adds the extension's context menus (Download with Kasi, Download all links, Download selected links) and the link batch sent to Kasi's picker. M2.5c adds the extension half of `refreshLink` (a fresh link for a paused job). M2.9c adds the download details window (segment map, one line per connection, log). M2.9b remembers the grid's columns, widths and sort order and adds a theme choice (follow Windows, light or dark). M2.9a gives the main window its toolbar, a sidebar with live counts (status, categories, queues), eight sortable columns, search, a status bar and keyboard shortcuts. M2.7 adds the store material: `npm run package` builds the Chrome, Edge and Firefox zips and the AMO source package, with listing text, permission justifications, a privacy policy and generated screenshots under `docs/store` (the submissions themselves are the owner's). M2.8e adds an optional clipboard watcher (off by default): copy a download link and Kasi offers to add it. M2.8c adds the tray icon (total speed, pause or resume everything, speed limit presets, quit), hides the window on close instead of exiting, and shows a toast when a download finishes or fails. M2.8d-4 adds the refresh-link dialog: an expired download link is replaced by reopening the page it came from in the browser. M2.8d-3 adds the link picker for "Download all links" and "Download selected links" from the browser. M2.8d-2 adds the Add download window and the duplicate prompt (a browser download opens it pre-filled, or is added at once if you switch that off in Settings). M2.8d-1 hosts the download engine in the app (database migrated at start-up, after the browser pipe is open) and shows the jobs in the main window. The rest of the desktop UI follows in later M2 tasks.

## Repository layout

```
kasi-dm/
├── src/
│   ├── Kasi.Core/            Domain model (Job, Segment, Queue, Category), interfaces, events
│   ├── Kasi.Engine/          Probe, HTTP/FTP transfer, dynamic segmentation, file writer, throttling, retry
│   ├── Kasi.Media/           HLS/DASH parsers, loader/DRM gate, segment runner and FFmpeg mux (M3.1–3.4)
│   ├── Kasi.Torrent/         MonoTorrent adapter (placeholder, M4)
│   ├── Kasi.Scheduling/      Queue runner, schedules, post-actions, wake timers (placeholder, M2)
│   ├── Kasi.Data/            SQLite repositories, migrations, checkpoint writer
│   ├── Kasi.Ipc/             Strict message contracts and serializer, hardened named-pipe client/server (M2.1)
│   ├── Kasi.App/             Avalonia desktop app: composition root, views, view models, tray (empty shell)
│   └── Kasi.NativeHost/      Native AOT browser native-messaging host: frame reader/writer, two-way relay, Kasi launcher (M2.2)
├── extension/                TypeScript Manifest V3 extension (Chromium and Firefox, M2.3a and M2.3b)
├── tests/
│   ├── Kasi.Engine.Tests/    Unit + property tests (FsCheck)
│   ├── Kasi.*.Tests/         One test project per src project
│   ├── Kasi.TestServer/      Controllable HTTP test server with chaos middleware
│   ├── Kasi.FaultInjection/  Kill-loop, power-cut and soak harnesses (planned, M1.14)
│   └── fixtures/             Content-Disposition corpus, IPC sample messages, hand-written HLS/DASH manifests (torrent files planned, M4)
├── bench/                    Kasi.Bench micro-benchmarks and macro modes, sources.json, results/
├── build/                    Packaging (Velopack), signing, FFmpeg fetch script (planned, M3/M4)
├── docs/                     Architecture decision records (ADRs), specs, per-milestone plans, runbooks, protocol/ (message schema)
├── Directory.Build.props     Shared MSBuild settings (nullable, warnings as errors, analyzers)
├── Directory.Packages.props  Central package versions
├── .editorconfig             Code style, enforced in build
└── Kasi.slnx
```

**Architecture rule:** nothing under `Kasi.Core`, `Kasi.Engine`, `Kasi.Media`, `Kasi.Torrent`, `Kasi.Scheduling`, `Kasi.Data` or `Kasi.Ipc` may reference Avalonia (KASI-NFR-019). The engine must run headless. An architecture test in `Kasi.ArchitectureTests` fails the build if this is broken.

## Prerequisites

| Tool | Version | Used for |
| --- | --- | --- |
| Windows | 10 22H2 or 11 | Development and running the app |
| .NET SDK | 10.0 (see `global.json`) | All .NET projects |
| Node.js | 22 LTS | Extension build and tests |
| PowerShell | 7.4+ | Build scripts |
| Docker Desktop | Current | Testcontainers (FTP, tracker, proxies) for integration tests |
| IDE | Visual Studio 2026, JetBrains Rider, or VS Code with C# Dev Kit | Avalonia XAML previewer available in all three |

## Getting started

```powershell
git clone https://github.com/emartsoft/kasi-dm.git
cd kasi-dm

dotnet restore

# Build everything
dotnet build -c Debug

# Run the desktop app (a second run with a URL hands it to the first; `--tray` starts without a window)
dotnet run --project src/Kasi.App
```

`build/fetch-ffmpeg.ps1` (pinned LGPL FFmpeg build) arrives with media capture in M3.

### Browser extension (development, from M2)

The extension build exists; native host registration is described below (M2.6).

```powershell
cd extension
npm ci
npm run build:chromium      # output: extension/dist/chromium
npm run build:firefox       # output: extension/dist/firefox
```

Load `dist/chromium` via `chrome://extensions` → Developer mode → Load unpacked. Then publish and register the development native host so the extension can reach your local build (`Kasi.exe` must sit next to the host for the browser to be able to start Kasi):

```powershell
dotnet publish src/Kasi.NativeHost -c Release -r win-x64
pwsh ./scripts/native-host/Register-NativeHost.ps1 -ChromeExtensionId <unpacked extension ID>
```

This writes `com.martsoft.kasi` host manifests under `%LOCALAPPDATA%\Kasi\NativeHost` and registers them under `HKCU` for Chrome, Edge, Chromium and Firefox. Each manifest allows the store IDs in `extension/store-ids.json` and any IDs you add with `-ChromeExtensionId` (Chrome, Chromium), `-EdgeExtensionId` or `-FirefoxExtensionId`; no other extension can start the host (`TC-BRW-007`). Run the script with `-Unregister` to undo (it removes only what it wrote). The installer takes over this job in M4.

## Testing

```powershell
# Unit and integration tests (needs Docker for Testcontainers)
dotnet test

# Only fast unit tests (the SQLite persistence tests are tagged Integration, so they are not in this set)
dotnet test --filter "Category!=Integration"

# One test case from the test plan
dotnet test --filter "TC=TC-ENG-008"

# Extension tests
cd extension; npm run check:types; npm run lint; npm run format:check; npm test; npm run build

# UI tests: the real Kasi.App window through UI Automation (FlaUI); needs an interactive desktop, skipped otherwise
$env:KASI_UI='1'; $env:KASI_UI_INPUT='1'; dotnet test tests/Kasi.App.UiTests

# Coverage report (engine gate requires 80% line coverage)
dotnet test --collect:"XPlat Code Coverage"
# the gate itself, on a merged Cobertura report from ReportGenerator:
pwsh build/Test-CoverageGate.ps1 -Cobertura coverage/Cobertura.xml -Assembly Kasi.Engine -Threshold 0.80
```

The container conformance tests (`Category=ContainerIntegration`, `tests/Kasi.Engine.Tests/Containers`) run on the Ubuntu CI leg and skip themselves without Docker (`KASI_REQUIRE_DOCKER=1` makes a missing Docker a failure); locally, start Docker and run `dotnet test tests/Kasi.Engine.Tests --filter "Category=ContainerIntegration"`.

The disk-full test (`TC-ENG-015`, `PartFileVhdIntegrationTests`) mounts a small VHDX with `diskpart` and skips itself unless you run elevated; CI sets `KASI_REQUIRE_VHD=1`, which turns a skip into a failure.

The native host's AOT executable sample (`TC-BRW-006`, `AotExecutableTests`, `Category=AotSample`) needs the published executable. It skips itself without `KASI_NATIVEHOST_AOT`; CI sets that and `KASI_REQUIRE_AOT=1`, which turns a skip into a failure. Locally (the Visual Studio C++ tools must be installed and `vswhere.exe` on `PATH`):

```powershell
dotnet publish src/Kasi.NativeHost -c Release -r win-x64 -o artifacts/native-host
pwsh build/Measure-NativeHost.ps1 -Exe artifacts/native-host/Kasi.NativeHost.exe   # size limit 5 MB, start-up median
$env:KASI_NATIVEHOST_AOT = (Resolve-Path artifacts/native-host/Kasi.NativeHost.exe).Path
dotnet test tests/Kasi.NativeHost.Tests --filter "Category=AotSample"
```

Micro-benchmarks (BenchmarkDotNet) live in `bench/Kasi.Bench`:

```powershell
dotnet run -c Release --project bench/Kasi.Bench -- --filter "*SegmentScheduler*"
```

Macro modes (see [docs/benchmarks.md](docs/benchmarks.md)): `throughput`, `local-1gbit`, `resources` and `compare`.

```powershell
dotnet build -c Release
dotnet bench/Kasi.Bench/bin/Release/net10.0/Kasi.Bench.dll local-1gbit --size 4294967296 --connections 16
```

Tests never reach the public internet; they run against `Kasi.TestServer`. Fault-injection and benchmark suites run on dedicated machines; see `KASI-TPL-2026-001`, sections 6 and 7.

## Packaging (from M4)

`build/pack.ps1` does not exist yet; the command below is the intended interface.

```powershell
pwsh ./build/pack.ps1 -Version 1.0.0 -Channel beta
```

Produces the Velopack installer, portable zip and delta packages for x64 and ARM64 in `artifacts/`. Signing runs only in CI with the release certificate.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. It covers branching, commit messages, code style, the definition of done and review rules.

## Licence

Kasi is free and open source under the [MIT licence](LICENSE). Third-party notices are in `THIRD-PARTY-NOTICES.txt`.
