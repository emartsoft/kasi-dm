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

## Repository layout

```
kasi/
├── src/
│   ├── Kasi.Core/            Domain model (Job, Segment, Queue, Category), interfaces, events
│   ├── Kasi.Engine/          Probe, HTTP/FTP transfer, dynamic segmentation, file writer, throttling, retry
│   ├── Kasi.Media/           HLS/DASH parsing, DRM detection, segment download, FFmpeg wrapper
│   ├── Kasi.Torrent/         MonoTorrent adapter
│   ├── Kasi.Scheduling/      Queue runner, schedules, post-actions, wake timers
│   ├── Kasi.Data/            SQLite repositories, migrations, checkpoint writer
│   ├── Kasi.Ipc/             Message contracts, JSON schema validation, named-pipe client/server
│   ├── Kasi.App/             Avalonia desktop app: composition root, views, view models, tray
│   └── Kasi.NativeHost/      Native AOT browser native-messaging host
├── extension/                TypeScript Manifest V3 extension (Chromium and Firefox builds)
├── tests/
│   ├── Kasi.Engine.Tests/    Unit + property tests (FsCheck)
│   ├── Kasi.*.Tests/         One test project per src project
│   ├── Kasi.TestServer/      Controllable HTTP test server with chaos middleware
│   ├── Kasi.FaultInjection/  Kill-loop, power-cut and soak harnesses
│   └── fixtures/             HLS/DASH manifests, torrent files, Content-Disposition cases
├── bench/                    Kasi.Bench harness, sources.json, weekly results
├── build/                    Packaging (Velopack), signing, FFmpeg fetch script
├── docs/                     Architecture decision records (ADRs), protocol schema, runbooks
├── Directory.Build.props     Shared MSBuild settings (nullable, warnings as errors, analyzers)
├── Directory.Packages.props  Central package versions
├── .editorconfig             Code style, enforced in build
└── Kasi.sln
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
git clone https://github.com/martsoft/kasi.git
cd kasi

# Restore .NET packages and fetch the pinned LGPL FFmpeg build into build/ffmpeg
dotnet restore
pwsh ./build/fetch-ffmpeg.ps1

# Build everything
dotnet build -c Debug

# Run the desktop app
dotnet run --project src/Kasi.App
```

### Browser extension (development)

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

# Only fast unit tests
dotnet test --filter "Category!=Integration"

# One test case from the test plan
dotnet test --filter "TC=TC-ENG-008"

# Extension tests
cd extension; npm test; npm run test:e2e

# Coverage report (engine gate requires 80% line coverage)
dotnet test --collect:"XPlat Code Coverage"
```

Tests never reach the public internet; they run against `Kasi.TestServer`. Fault-injection and benchmark suites run on dedicated machines; see `KASI-TPL-2026-001`, sections 6 and 7.

## Packaging

```powershell
pwsh ./build/pack.ps1 -Version 1.0.0 -Channel beta
```

Produces the Velopack installer, portable zip and delta packages for x64 and ARM64 in `artifacts/`. Signing runs only in CI with the release certificate.

## Contributing

Read [CONTRIBUTING.md](CONTRIBUTING.md) before opening a pull request. It covers branching, commit messages, code style, the definition of done and review rules.

## Licence

[To be decided: see TRD Appendix B, open issue 2.] Third-party notices are in `THIRD-PARTY-NOTICES.txt`.
