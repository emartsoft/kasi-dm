# Kasi Download Manager

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

Under construction, in milestone **M1 (download engine)**; see [plan.md](plan.md) for the task list and what is done. Today the repository has the domain model and job lifecycle, the SQLite data layer with the checkpoint writer, filename handling, the HTTP probe, the segment scheduler, the part-file disk writer and the HTTP transfer loop with a per-worker TLS/HTTP2 connection pool and transient-error retry, plus `Kasi.TestServer` and the CI pipeline. **End-to-end HTTP downloads work**, including the live slow-lane, endgame and split scenarios; retry and checkpoint orchestration (M1.8/M1.9), auth, bandwidth limiting, FTP and the fault-injection harness remain. The desktop app is still an empty shell. `Kasi.Media`, `Kasi.Torrent`, `Kasi.Scheduling` and `Kasi.Ipc` are placeholders, and the browser extension, native host, FFmpeg fetch, packaging scripts and fault-injection harness arrive in later milestones. Items in the layout below marked *(planned)* do not exist yet.

## Repository layout

```
kasi-dm/
├── src/
│   ├── Kasi.Core/            Domain model (Job, Segment, Queue, Category), interfaces, events
│   ├── Kasi.Engine/          Probe, HTTP/FTP transfer, dynamic segmentation, file writer, throttling, retry
│   ├── Kasi.Media/           HLS/DASH parsing, DRM detection, segment download, FFmpeg wrapper (placeholder, M3)
│   ├── Kasi.Torrent/         MonoTorrent adapter (placeholder, M4)
│   ├── Kasi.Scheduling/      Queue runner, schedules, post-actions, wake timers (placeholder, M2)
│   ├── Kasi.Data/            SQLite repositories, migrations, checkpoint writer
│   ├── Kasi.Ipc/             Message contracts, JSON schema validation, named-pipe client/server (placeholder, M2)
│   ├── Kasi.App/             Avalonia desktop app: composition root, views, view models, tray (empty shell)
│   └── Kasi.NativeHost/      Native AOT browser native-messaging host (stub, M2)
├── extension/                TypeScript Manifest V3 extension (Chromium and Firefox builds) (planned, M2)
├── tests/
│   ├── Kasi.Engine.Tests/    Unit + property tests (FsCheck)
│   ├── Kasi.*.Tests/         One test project per src project
│   ├── Kasi.TestServer/      Controllable HTTP test server with chaos middleware
│   ├── Kasi.FaultInjection/  Kill-loop, power-cut and soak harnesses (planned, M1.14)
│   └── fixtures/             Content-Disposition corpus (HLS/DASH manifests and torrent files planned, M3/M4)
├── bench/                    Kasi.Bench BenchmarkDotNet micro-benchmarks (sources.json and weekly results planned, M1.15)
├── build/                    Packaging (Velopack), signing, FFmpeg fetch script (planned, M3/M4)
├── docs/                     Architecture decision records (ADRs), specs, per-milestone plans, runbooks
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

# Run the desktop app (an empty shell until M2)
dotnet run --project src/Kasi.App
```

`build/fetch-ffmpeg.ps1` (pinned LGPL FFmpeg build) arrives with media capture in M3.

### Browser extension (development, from M2)

The extension, the dev host registration script and the commands below do not exist yet.

```powershell
cd extension
npm ci
npm run build:chromium      # output: extension/dist/chromium
npm run build:firefox       # output: extension/dist/firefox
```

Load `dist/chromium` via `chrome://extensions` → Developer mode → Load unpacked. Then register the development native host so the extension can reach your local build:

```powershell
pwsh ./build/register-dev-host.ps1 -ExtensionId <your unpacked extension ID>
```

This writes `com.martsoft.kasi` host manifests under `HKCU` pointing at your Debug build of `Kasi.NativeHost`. Run `./build/register-dev-host.ps1 -Remove` to undo.

## Testing

```powershell
# Unit and integration tests (needs Docker for Testcontainers)
dotnet test

# Only fast unit tests (the SQLite persistence tests are tagged Integration, so they are not in this set)
dotnet test --filter "Category!=Integration"

# One test case from the test plan
dotnet test --filter "TC=TC-ENG-008"

# Extension tests
cd extension; npm test; npm run test:e2e

# Coverage report (engine gate requires 80% line coverage)
dotnet test --collect:"XPlat Code Coverage"
```

The disk-full test (`TC-ENG-015`, `PartFileVhdIntegrationTests`) mounts a small VHDX with `diskpart` and skips itself unless you run elevated; CI sets `KASI_REQUIRE_VHD=1`, which turns a skip into a failure.

Micro-benchmarks (BenchmarkDotNet) live in `bench/Kasi.Bench`:

```powershell
dotnet run -c Release --project bench/Kasi.Bench -- --filter "*SegmentScheduler*"
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
