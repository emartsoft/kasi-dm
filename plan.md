# Kasi delivery plan: M0 to v1.0

Working plan for building Kasi from the foundations (M0) to the v1.0 release (M5). It turns the roadmap in TRD section 16 and the gates in TPL section 4 into ordered tasks, each with the requirement and test-case IDs it covers and how it is verified.

- **Sources of truth:** TRD `docs/specs/KASI-TRD-2026-001.md` (what to build), test plan `docs/specs/KASI-TPL-2026-001.md` (how it is proven), `CONTRIBUTING.md` (how we work). If this file disagrees with them, they win; fix this file.
- **Last updated:** 2026-10-23, with M2.9d (10,000-row budget, keyboard/accessibility coverage, Mica backdrop) implemented, M2.9c (details window, segment map, `SegmentsProgressed` and `JobLogged` engine events, `TC-UI-002`) implemented, M2.9b (saved column layout and sort, theme override) implemented, M2.9a (main window: toolbar, sidebar, eight columns, search, status bar, shortcuts, delete confirmation) implemented, the M2 task statuses corrected against what is merged (M2.1, M2.3 and M2.5 closed, M2.7 and M2.8 no longer shown as not started) and the second review of M2.1 waived by the owner, M2.7 (store packager, listing text, justifications, privacy policy, generated screenshots and promo tiles; the submissions are owner steps) implemented, M2.8e (clipboard capture, `TC-BRW-008`) implemented, M2.8c (tray, close to tray, completion toasts) implemented, M2.8d-4 (refresh-link dialog and the `refreshLink` round trip with the extension) implemented, M2.8d-3 (link picker, `TC-BRW-005` Kasi half) implemented, M2.8d-2 (Add download dialog, duplicate prompt, site memory, the browser-dialog switch) implemented, M2.8d-1 (engine hosted in the app, `IJobService`, job list) implemented, M2.5c (`refreshLink`, extension half) implemented, M2.5b (context menus, link batch; `TC-BRW-005` extension half end to end) implemented, M2.6 (store IDs, registration script options, `TC-BRW-007`) implemented, M2.8b (Kasi's side of the browser protocol, rules sender, Settings skeleton) implemented, M2.8a (pipe-first start-up, single instance and argument hand-off, UI batch dispatcher; ADR 0023 started) implemented, M2.5a (connection status after Kasi quits, interception toggle) implemented, the M2.5 to M2.8 plans rewritten after review (decisions approved), M2.2a and M2.2b (native messaging host, its fuzzing and AOT publish check; ADR 0017) implemented, M2.1 (protocol contracts and the named pipe; PRs #53, #54) merged, M2.3a extension scaffold implemented M2.3b extension behaviour implemented M2.4a rule semantics, M2.4b interception logic and M2.4c end-to-end harness implemented; M1.1 to M1.14 merged, M1.15 implemented pending the owner's IDM sessions, and the M1 engine gate still open.
- **Estimates:** week numbers are the TRD's (24 weeks for v1.0, engine first). The TRD says estimates are confirmed at the end of M0. Sub-task weeks below are indicative.
- **Status marks:** in the task tables ✅ done and verified, 🔶 partly done, ⬜ not started; only the gate checklists keep `- [ ]` boxes. Update this file in the same PR that finishes a task.

## 1. Roadmap at a glance

| Phase | Weeks | Theme | Gate at the end | Status |
| --- | --- | --- | --- | --- |
| M0 | 1 to 2 | Foundations: solution, data layer, test server, CI | End of M0 | ✅ closed for engineering; owner items open (code signing, Native AOT check) |
| M1 | 3 to 8 | Download engine, resume, bandwidth | **Engine gate** | 🔶 16 of 16 tasks done (M1.1 to M1.16); the engine gate itself is still open |
| M2 | 9 to 13 | Browser extension, host, queues, categories, main UI flows | **Internal alpha** | 🔶 M2.1 merged; M2.3a and M2.3b implemented |
| M3 | 14 to 17 | Media capture, FFmpeg, scheduler wake and quotas | none (plan v0.4) | ⬜ not started |
| M4 | 18 to 21 | BitTorrent, installer, portable, updates, signing | **Public beta** | ⬜ not started |
| M5 | 22 to 24 | UI polish, accessibility, localisation, full regression, release | **v1.0 release** | ⬜ not started |

Status markers in the task tables: ✅ done, 🔶 in progress or partly done, ⬜ not started. (Markdown shows `[x]` as plain text inside tables, so tables use these symbols; the gate checklists below keep `- [ ]` boxes.)

Assumed team (TRD 16): one lead developer (.NET), two developers (.NET/Avalonia), one extension and host developer from M2, one QA engineer from M1. A fourth developer would run M3 and M4 in parallel and shorten the plan to about 20 weeks. A P2 requirement not finished by the beta gate moves to v1.1 with Technical Manager approval; P1 cannot slip.

## 2. Rules that apply to every task

Taken from `CLAUDE.md` and `CONTRIBUTING.md`; repeated here because every task below depends on them.

1. **Plan first.** Anything larger than a small fix gets a short plan naming its KASI and TC IDs, approved before code.
2. **Small steps.** Each step builds and passes tests before the next starts. One Conventional Commit per step (scopes: `engine`, `media`, `torrent`, `scheduling`, `data`, `ipc`, `app`, `host`, `extension`, `build`). PRs stay focused, ideally under 400 lines of non-generated diff.
3. **Tests carry IDs.** `[Trait("TC", "TC-ENG-008")]`; integration tests also `[Trait("Category", "Integration")]`. A defect fix starts with a failing test. Download tests verify SHA-256. Tests never touch the public internet.
4. **Hard rules:** engine has no UI dependency (enforced by `Kasi.ArchitectureTests`); bytes are recorded as written only after `FlushFileBuffers`; no URLs with query strings, cookies, headers, tokens or full user paths in logs; DRM refusal is never configurable; async all the way with `ConfigureAwait(false)` in libraries; `TimeProvider` not `DateTime.Now`; no per-chunk allocation on hot paths.
5. **Dependencies.** Anything not in the CLAUDE.md stack list needs approval and a stated licence, and goes into `Directory.Packages.props` with its licence in the comment. No GPL.
6. **Reviews.** Two approvals (including the lead developer) for engine segmentation, resume, checkpointing, `Kasi.Data` migrations, and for security-sensitive code (`Kasi.Ipc`, `Kasi.NativeHost`, extension messaging, DRM detection, secret storage, update verification; tag `security`). One for most other code.
7. **Definition of done** (CONTRIBUTING 6): merged through an approved PR with green CI; acceptance criteria pass with tagged tests; no new analyzer warnings and coverage not reduced; logs clean of sensitive data; UI keyboard-accessible with automation names and light/dark; user-visible text in resource files; benchmark results for performance-sensitive changes (no regression beyond 5%); docs, ADR, protocol schema or migration updated where behaviour changed.
8. **Defects.** GitHub Issues with severity label and TC ID. Critical blocks every gate; High blocks the next gate (TPL 8).

## 3. M0: Foundations (weeks 1 to 2)

### 3.1 Delivered

Delivered in M0 (PRs #1 to #5, all merged into `main`); the step commits below were squash-merged. 159 tests passed at the time, and a fresh clone passed the CI commands.

| Step | Commit | Delivered | Verified by |
| --- | --- | --- | --- |
| ✅ 0 | `31668b9` | ADR template (`docs/adr/0000-template.md`), local branch renamed to `main` | files exist |
| ✅ 1 | `e8ce817` | `Kasi.slnx`, 9 projects from TRD 5.2, `Directory.Build.props`, `Directory.Packages.props` (central package management), `global.json` (.NET 10), repo-local `nuget.config`, `src/.editorconfig` making `CA2007` an error in libraries, extension placeholder | Debug and Release build with 0 warnings; `dotnet format --verify-no-changes`; `CA2007` fires in `Kasi.Core` and not in `Kasi.App` |
| ✅ 2 | `958e8b8` | `Kasi.ArchitectureTests`: no Avalonia in the resolved graph or compiled references of UI-free projects; project references match TRD 5.2 (KASI-NFR-019) | 26 tests; red run proven by adding Avalonia to `Kasi.Engine` (3 failures), then reverted |
| ✅ 3 | `4b647bf`, `f010136` | `Kasi.Data`: connection factory (WAL, `synchronous=NORMAL`, foreign keys, fails loudly if WAL is refused), migration runner (embedded `NNNN_name.sql`, one transaction, `.bak` snapshot via the backup API, refuses newer databases), migration `0001` = TRD 11.2 | 16 integration tests; forcing `journal_mode=DELETE` fails 15 of 16 |
| ✅ 4 | `d9af857`, `3cc101f` | `Kasi.TestServer`: `/files`, `/norange`, `/chunked`, `/mutable`, `/redirect`, `/auth/basic`, `/auth/cookie`, `/signed`, `/disposition`, `/hls`, `/dash`; chaos middleware with all 8 faults, seeded, via `X-Kasi-Chaos` or `POST /_chaos`; contract in ADR 0001 | 114 tests, SHA-256 oracle on downloads, 4 GiB + 1 and 20 GiB offsets, seeded replay |
| ✅ 5 | `bf83b46` | `Kasi.App` Avalonia 12 shell: FluentAvalonia theme, Hosting composition root, empty main window with automation name, strings in `.resx` | 3 headless tests (Light and Dark); real exe shows a window titled "Kasi" and exits 0 |
| ✅ 6 | `c094532` | `.github/workflows/ci.yml`: restore, format check, Release build, unit and integration tests with Coverlet, ReportGenerator summary and artifact (tool pinned in `dotnet-tools.json`) | all commands run in a fresh clone; coverage 91.1% lines over `Kasi.App` and `Kasi.Data`; YAML parses |

Dependencies added in M0 (all MIT unless noted): Avalonia 12.1.4 (+ Desktop, Headless.XUnit), FluentAvaloniaUI 3.1.0, CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting, Microsoft.Data.Sqlite, MinVer, coverlet.collector, Microsoft.VisualStudio.Threading.Analyzers, xunit.v3 (Apache-2.0), AwesomeAssertions (Apache-2.0; replaces FluentAssertions, whose v8+ is commercially licensed), ReportGenerator (Apache-2.0, tool only).

### 3.2 Closing M0

End-of-M0 exit criteria (TPL 4): repository, CI and signing set up; TestServer and chaos merged; CI green on an empty engine; defect tracker configured; test plan approved.

M0 is **closed for engineering** (2026-10-17): PRs #1 to #5 are squash-merged into `main` and CI is green there. The rows below that are still open are owner decisions; they gate the end-of-M0 review, not M1 coding.

| Task | Owner | Status |
| --- | --- | --- |
| ✅ Remote created, work pushed, PRs opened and merged (#1 to #5), default branch `main`, step branches deleted | you, dev | done |
| ✅ CI green on GitHub (Windows runner), workflow runs on PRs to any base branch | dev | done |
| ✅ Defect tracker: `bug`, `needs-triage`, `security` and four `severity:` labels created; bug template has severity and linked-test-case fields | dev | done |
| ✅ Set `CLAUDE.md` "Current phase" to M1 | dev | done |
| ✅ Protect `main`: ruleset `main-protect` is active: no deletion, no force-push, pull request required (0 approvals while there is one developer), squash merge only, required checks **Build, test, coverage** and **Build and unit tests (ARM64)**. Raise the approval count to match CONTRIBUTING 5.2 when a second developer joins | you | done 17 Oct 2026 |
| ✅ Approve test plan v0.1 and the TRD | CTO | done: signed MM, 17 Oct 2026 in both document-control tables |
| 🔶 Code-signing certificate (Appendix B #4): route chosen 17 Oct 2026, **SignPath Foundation** (free for open source). SignPath organisation and a self-signed test certificate set up 18 Oct 2026 (test signing only). Still to do: apply for the open-source programme at signpath.org, add the code signing policy section SignPath requires to the README, and design the signing flow (build in GitHub Actions, sign binaries, `vpk pack`, sign the installer). Deadline set to the end of M4, but M4.6 signs the beta builds, so it must be ready when M4.6 starts. Fallback: EV certificate on cloud signing | you | accepted by SignPath; secrets and project slug in CI secret storage only |
| ✅ Choose the ARM64 runner: GitHub-hosted `windows-11-arm` (free for public repos) for build and unit tests on ARM64; an x64 runner cross-publishes `win-arm64` artifacts; a Windows 11 ARM64 VM covers release-candidate smoke runs | lead dev | done 17 Oct 2026; the CI job is added in its own PR, the release job in M4 |
| ✅ Product name: **Kasi Download Manager** (Appendix B #1) | you | done; a trademark search in Kenya and key markets is still advisable before the first public build |
| ✅ Confirm the 24-week estimate and sub-task weeks in this file | CTO | done: approved 17 Oct 2026 |
| ⬜ Verify Native AOT publish of `Kasi.NativeHost` (link step failed on the author's machine: `vswhere.exe` not found); moves to M2 when the host has code | dev | `dotnet publish -r win-x64` succeeds locally or in CI |

## 4. M1: Download engine, resume, bandwidth (weeks 3 to 8)

**Goal:** a UI-free engine that downloads any HTTP file as fast as IDM and never loses or corrupts a byte. Everything else depends on it, so quality here decides the product.

**Start-of-M1 items:** appoint the QA lead; procure the benchmark machine and an IDM test licence by week 4 (TPL 10); approve new dependencies as they come up (see 4.3).

### 4.1 Tasks, in dependency order

| # | Task | Covers | Verified by |
| --- | --- | --- | --- |
| ✅ M1.1 | **Domain model** in `Kasi.Core`: `Job`, `Segment`, `Queue`, `Category`, `JobId` (UUIDv7), the 9-state job lifecycle (state names read from the diagram in the TRD `.docx`; rules in ADR 0002), engine event records, `Redact` helpers in `Kasi.Core.Logging`. `KasiFeatures` is deferred until the first incomplete feature needs a flag | groundwork, NFR-013 | done: `Kasi.Core.Tests` (109 tests: all 81 state pairs per kind, segment invariants, redaction cases) and `Kasi.Data.Tests` drift guards (entity properties equal table columns; enum texts pass the CHECK constraints) |
| ✅ M1.2 | **Persistence**: Dapper repositories for job, segment, queue, category (contracts in `Kasi.Core`); migration `0002` (`setting`, queue 1 "Main", default categories with relative folders); the `CheckpointWriter` (one transaction per 2 s cycle, only for cursors flushed first, never backwards, retried on failure); ADR 0003 | RES-001, RES-006, NFR-009, TRD 11.3 | done: `Kasi.Data.Tests` 73 tests on real SQLite, writer timer driven by `FakeTimeProvider`. Kill and power-cut proofs (TC-RES-003, TC-RES-011) stay with M1.14 |
| ✅ M1.3 | **Filename handling**: `Content-Disposition` (RFC 6266, `filename*`), URL fallback, Latin-1 → UTF-8 repair on plain `filename`, sanitiser (separators, drive prefixes, control and bidi characters, `CON`/`NUL` including stems with trailing dots or spaces, 200-character cap preserving extension and surrogate pairs) | ENG-003, TC-ENG-004, 005 | done: `Kasi.Engine.Tests` (173 tests: unit, live `/disposition` integration + drift guard, seed corpus and seeded fuzz target). Nightly `SharpFuzz` runner stays with M1.16 |
| ✅ M1.4 | **Probe** with `Range: bytes=0-`: `206` keeps streaming as segment 0, `200` means single connection and non-resumable, chunked means unknown size, redirects (max 10), `416` re-probe, validators recorded | ENG-003, ENG-007, RES-005, TC-ENG-003, 012, 013, TC-RES-010 | done: `Kasi.Engine.Tests` (206 tests: `ProbeOutcomeTests`, `HttpProberUnitTests`, `HttpProberIntegrationTests` against `/files`, `/novalidators`, `/norange`, `/chunked`, `/redirect`, `/disposition`, `/auth`, `/signed`, and chaos profiles) |
| ✅ M1.5 | **Segment scheduler** (pure logic first): leases sized to 2 s of speed (512 KiB to 16 MiB), `TrySplit` with `minSplit` 1 MiB and 64 KiB alignment, slow-lane takeover (below 30% of median for 10 s), endgame duplication (cap 2 MiB), connection ramp-up (one more every 250 ms while throughput rises 5%) | ENG-004, ENG-005, TC-ENG-006 to 009, 011 | done as simulation: FsCheck property test (segments never overlap, never gap, union = file, sizes 0 B to 64 GiB), 50 KB/s slow lane, endgame budget, ramp-up to 32; ADR 0004; `bench/Kasi.Bench` micro-benchmark (`AdvanceLease` about 60 ns, no per-chunk allocation). The live chaos version moves to M1.7 |
| ✅ M1.6 | **Disk writer**: one sparse `.kasipart` file, `RandomAccess.WriteAsync` with pooled 256 KiB buffers (`PooledPartWriteBuffer`), `FlushFileBuffers`, free-space check (`GetDiskFreeSpaceExW`) and typed `NotEnoughSpace` on `ERROR_DISK_FULL`/quota errors, two-phase finalise (`SealAsync` clears sparse flag and verifies length + `FSCTL_QUERY_ALLOCATED_RANGES` completeness, `CommitAsync` renames), ADR 0005. Mark-of-the-Web is written in M2 (CAT-003) | ENG-008, RES-001, TC-ENG-014, TC-ENG-015 (writer half) | done: `Kasi.Engine.Tests` (34 writer/space/buffer tests + `PartFileVhdIntegrationTests` on a 64 MiB NTFS VHDX with `KASI_REQUIRE_VHD=1` in CI; allocation tests + `PartFileWriteBenchmarks` prove 0 B per buffered 16 KiB chunk and 389 B per 256 KiB OS write) |
| ✅ M1.7 | **HTTP transfer**: `SocketsHttpHandler` pool per host, `EnableMultipleHttp2Connections`, connection reuse, TLS validation always on with no bypass. Extend `Kasi.TestServer` first with TLS and HTTP/2 (generated certificate; ADR 0001 notes it is missing) and handshake counters | ENG-001, ENG-006, NFR-011, TC-ENG-001, 002, 010, 015 (job-level transfer loop half), 023 | **done (M1.7a PR #20, M1.7b PR #21, M1.7c this PR):** TLS modes/HTTP2/metrics and per-worker `HttpHostPool`; `HttpTransferRunner` fresh downloads, `/norange`, `/chunked`, 0 B, resume with `If-Range`, cancellation, changed-file and disk-full preservation; 1 GiB HTTP/1.1 and HTTP/2 downloads match SHA-256, connection counts 1/2/8/16/32 on 256 MiB, `TlsHandshakesCompleted <= 10` and no aborted leases. live scenarios `TC-ENG-008` (a 50 KB/s connection among eight; the run is bounded in time and keeps the healthy rate after the 10 s window, and fails with the takeover disabled), `TC-ENG-009` (endgame duplicates at most 2 MiB) and `TC-ENG-011` (splits in flight, zero aborted requests) |
| ✅ M1.8 | **Errors and retry**: classify (transient, throttled, auth or expired, permanent, file changed, offline); exponential backoff with full jitter 1 s to 60 s, 10 attempts; `429`/`503` `Retry-After` and one fewer connection for that host; offline and `DiskErrorKind.NotEnoughSpace` outcomes returned classified with a durable snapshot (the job-level pause and resume belong to M1.13) | RES-004, TC-RES-007, 008, 009, 012 | **merged (#23):** `TransferRetryPolicy` classification and backoff (progress-based retry budget, `Retry-After` cap, read-stall timeout, transfer-wide throttle window with one connection reduction) with probe and lease retries; live `503` burst, the TPL profile (30% `503` plus a reset every 5 MiB), a connection dying every 128 KiB, `429` on the probe and on three leases at once, permanent `404`, truncation and a stalled read. **Still open (moved to the engine facade, M1.13):** job-level pause on disk full and offline pause/resume. Chaos tests: `503` bursts, resets every 5 MB, truncation; fake `TimeProvider` checks backoff intervals |
| ✅ M1.9 | **Checkpoint and resume**: flush then record cursors every 2 s and on pause and shutdown; resume with `If-Range`; `200` means the file changed, so restart and warn; crash-safe ordering | RES-001, 002, 003, 006, TC-RES-001 to 006, 011 | **merged (#24):** `SegmentCheckpointCoordinator` snapshots under the scheduler lock, flushes, persists split structure and records cursors every 1.5 s (loss on a kill at most 2 s with the write-buffer age), tolerates brief database failures and stops the transfer when they persist; probe metadata written with a targeted update; SQLite resume after cancel, `/mutable` change between/mid session, ordering proven with recording fakes. Orchestrator, recovery and `JobRestarted` moved to M1.13; kill-loop/power-cut/reboot harness remains M1.14. Two approvals |
| ✅ M1.10 | **Auth, cookies, redirects, refresh link** on the engine side: Basic, Digest, forwarded cookies and headers (cookies only to their own domain and path, never across hosts), `job_secret` stored with DPAPI, link refresh hands a new URL to a paused job. The extension half comes in M2 | ENG-009, ENG-010, TC-ENG-016 to 018 | **done (10a #27, 10b #28, 10c #29):** DPAPI secret storage and URL updates; scoped Basic/Digest credentials, captured headers and cookies, per-origin redirects; validated signed-link refresh resumes saved cursors and rejects changed length or ETag before writing. Live expiry, redirect, SHA-256 and secret-scope tests cover the flow. Plan in `docs/plans/m1-10.md`; ADR 0009. `System.Security.Cryptography.ProtectedData` (MIT) approved 18 Oct 2026 |
| ✅ M1.11 | **Bandwidth limiter**: two-level token bucket (global plus per-job, 16 KiB reads, capacity a quarter second), exposed so torrents can draw from it later | BWL-001, BWL-002, TC-ENG-024 | **merged (#32):** simulated and live ten-second rate checks (global, per job, two jobs sharing a cap, a rate change mid-download), SHA-256 transfers, stall-timer regression, whole-window reads when no cap is set, every slice written while capped (NFR-007), no endgame duplicates under a cap, and a BenchmarkDotNet micro-benchmark; [plan](docs/plans/m1-11.md) (section 5 lists where the build differs from the design), [ADR 0010](docs/adr/0010-shared-bandwidth-token-buckets.md). |
| ✅ M1.12 | **Remaining P2 engine features:** proxies HTTP, HTTPS, SOCKS5 (ENG-011, TC-ENG-019); checksum verification MD5, SHA-1, SHA-256 (ENG-012, TC-ENG-020); FTP and FTPS with FluentFTP and `REST` resume (ENG-002, TC-ENG-022); duplicate detection logic (ENG-013, TC-ENG-021 engine half) | as listed | **12a merged:** `ExpectedChecksum`/`ChecksumVerifier` verify MD5, SHA-1 or SHA-256 after sealing and before commit (`VerificationFailed` keeps the part file), `DuplicateDetector` (URL and name matching, auto-rename, legal actions). **12b merged:** `ProxySettings` (system, none or custom HTTP/HTTPS/SOCKS5 proxy, bypass hosts), proxy credentials in `JobSecret.ProxyCredentials`, an in-process test proxy (forward, CONNECT, SOCKS5, Basic auth) and proof that a proxy-only host cannot be reached directly. **12c merged (#35):** FTP and FTPS through the shared lease loop (`ILeaseTransport`, FluentFTP 55.1.0): passive mode, `REST` resume with size plus `MDTM` as identity, credentials only in the job secret, explicit or implicit TLS that never falls back to clear text, pinned test trust, shared bandwidth cap and checksum. In-process FTP/FTPS test server. 1 GiB FTP/FTPS runs are nightly evidence (M1.16). The M1.12 gate is the in-process test proxy and FTP servers on the Windows CI leg; Squid, Dante and vsftpd conformance runs move to the Ubuntu leg in M1.16. Plan in `docs/plans/m1-12.md`, ADR 0011 |
| ✅ M1.13 | **Engine facade**: orchestrator that runs jobs, owns the lifecycle, persists every transition before notifying, and runs headless in tests. Takes over what M1.8 and M1.9 left to it: resume-or-restart decisions and the restart-and-warn on a changed file (`JobRestarted`, capped restarts), startup crash recovery (including a crash after the rename), the recovery edges in the job state machine, job-level pause on disk full and pause-all/resume when offline (TC-ENG-015 job-level pause/resume half), and the kill-loop smoke test (TC-RES-001, 011, 004 engine half) | ENG-*, NFR-019, TC-ENG-015 (job-level half) | **13a merged:** `DownloadEngine` core: compare-and-set state changes (`TryChangeStateAsync`, never touching `done_bytes`), explicit recovery edges, stored pause reason, start-up recovery (`quick_check`, completed-after-rename, part-file reconcile, reset, `Verifying` finalised), duplicate-aware add, events through one dispatcher. **13b merged:** pause, resume, cancel and shutdown as distinct cancellations, disk full pauses with `NotEnoughSpace`, `INetworkStatus` with a debounced system implementation (offline pauses, reconnect resumes only network-paused jobs, also after a restart), link refresh as a pausable job. **13c merged (#38):** a changed remote file restarts the job (`JobRestarted`, old part file removed, stored restart count so the cap of 2 holds across restarts), start and resume return once the job runs (`WaitForJobAsync`), transitions are checked against the state machine, authentication failures pause with their own `AuthRequired` reason, a real `Verifying` state, live `JobProgressed` samples, network changes handled one at a time, a kill-loop smoke test that kills a child three times and bounds the refetch, and a headless end-to-end download. Plan in `docs/plans/m1-13.md`, ADR 0012 |
| ✅ M1.14 | **Fault-injection harness** (`Kasi.FaultInjection`): kill loop (200 kills nightly), power cut on a Hyper-V VM (20), 50-job network chaos soak through Toxiproxy (2 h), server mutation (50), disk pressure (20), storage stall (10), sleep and hibernate (manual, weekly). Seeds printed on failure | RES-001 to 006, NFR-007, TC-RES-001 to 004, 011 | **14a merged:** the existing `Kasi.FaultInjection` child grew into a harness (`--scenario kill|commit-kill`, seeded kill delays, JSON report plus `summary.md`, SHA-256 of every finished download); one iteration is one kill, with `PRAGMA integrity_check`, a `done_bytes` consistency check and a refetch bound after each; the commit kill uses a test-only `CheckpointWriter.BeforeCommit` hook. **14b merged:** `--scenario server-mutation` (one job per change, between or during a session, pass = never a mix), `storage-stall` (5 s write stalls against a 1 s read-stall timeout, nothing may be aborted or fetched twice) and `disk-pressure` (a full VHDX mounted in a folder: `NotEnoughSpace` pause, then resume after space is freed; skips itself without Administrator rights). **14c done:** `ToxiproxyClient` (REST calls, no package, unit-tested against a fake handler) and `--scenario network-soak`: one proxy per job, a seeded schedule of latency, a 64 KiB/s window, a reset and a 30 s partition, jobs spread over up to 2 h, a stuck-job check (no progress for 2 min in `Downloading`); it skips itself without a daemon. **14d done:** a guest-side `--scenario power-guest` (run, progress, verify; fixed test-server port, state folder, verdict file), `scripts/fault-injection/Invoke-HyperVPowerCut.ps1` (restore checkpoint, PowerShell Direct, `Stop-VM -TurnOff` at a seeded percentage or inside a commit, verify) and its self-hosted workflow (`power-cut.yml`, main and schedule only, never `pull_request`), and `nightly-faults.yml` with one matrix leg per suite and a pinned, hash-checked Toxiproxy. **Owner evidence still to come:** the 20 Hyper-V cuts and the weekly sleep/hibernate checklist; the first nightly run happens once this is on `main`. Plan in `docs/plans/m1-14.md`, ADR 0013. Nightly workflow publishes a report artefact (suite, seed, iterations, failures, checksum results) |
| ✅ M1.15 | **Benchmarks**: `Kasi.Bench`, `bench/sources.json` (six source types, TPL 6.2), alternating IDM/Kasi runs, weekly results in `bench/results`; first sessions from week 5; one tuning sprint before M2 if needed | NFR-001, NFR-002 | median ratio at most 1.10 and no source worse than 1.25 for the gate; at least 1 Gbit/s on a local 10 GbE server with 16 connections. **15a done:** `Kasi.Bench` macro modes (`throughput`, `local-1gbit`, `resources`, `compare`) over `DownloadEngine` with `Kasi.TestServer` in its own process, a raw `HttpClient` control for the 1 Gbit/s check (inconclusive, not failed, when the control is below target), engine-only resource sampling, a versioned session schema with the TPL 6.4 regression rules, and `bench/sources.json` with every archetype a marked placeholder plus local TestServer sources. **15b done:** `bench/Compare-Idm.ps1` (alternating IDM and Kasi runs from `sources.json` or `KASI_BENCH_URL_<ID>`, DNS flushed before every run, timing taken outside both tools, every run verified by SHA-256 against the first) and `Kasi.Bench scorecard` (median Kasi/IDM ratio per source and connection count against 1.10 and 1.25, `--release` for 1.05; unverified sources are excluded and listed, never counted as fast), with a dry run against a fake `IDMan` in CI. **Owner evidence still to come:** the real sessions at business hours and at night on the benchmark machine, with real sources in `bench/sources.json`. Plan in `docs/plans/m1-15.md`, ADR 0014 |
| ✅ M1.16 | **CI growth**: Ubuntu leg for Testcontainers, fuzzers for the filename parser, **turn on the 80% engine coverage gate** (`Kasi.Engine`), 3-consecutive-nights check | NFR-018 | gate fails a PR that lowers engine coverage below 80%. **16a done:** `build/Test-CoverageGate.ps1` reads the `Kasi.Engine` line-rate from the merged Cobertura report and fails below 80% (exit 1) or when the report or assembly is missing (exit 2); it is a step in `ci.yml` with no skip guard. `Kasi.Engine` measured **86.5%** with every test running, so the gate starts on. **16b done:** `ci-conformance-linux.yml` on Ubuntu runs `Category=ContainerIntegration` (the Windows jobs exclude it) against real vsftpd (passive FTP, explicit FTPS with a pinned certificate, `REST` resume, bad login), Squid (forward proxy, `CONNECT`, Basic auth) and Dante (SOCKS5 with username and password), built from one Dockerfile on Ubuntu 24.04 pinned by digest; `KASI_REQUIRE_DOCKER=1` turns a missing Docker into a failure. All eight cases ran against the real daemons (breaking the `REST` offset fails the vsftpd resume test); running them found three first-run problems, all fixed in the test setup, none in the engine. **16c done:** `tests/Kasi.Fuzz` (SharpFuzz 2.3.0, MIT) with the filename invariants in one place (`FileNameFuzz`, shared with the unit tests and the saved crash corpus in `tests/fixtures/fuzz/crashes`), `build/Run-Fuzz.ps1` (instruments `Kasi.Engine.dll`, libFuzzer 3 minutes, minimises findings) and `nightly-fuzz.yml` on Windows with a hash-pinned `libfuzzer-dotnet`; an injected index bug was found and minimised in under a minute. `build/Check-M1Gate.ps1` judges the six gate criteria from live evidence (tagged P1 cases, three scheduled nights with every suite PASS and none SKIPPED, the IDM scorecard, engine coverage from the latest main run, open Critical or High defects, `docs/m1-gate-evidence.json` for the Hyper-V power cut) and writes the gate report (`m1-gate.yml`, manual). **The gate itself stays open:** it needs three green nights, the power-cut and IDM evidence and the sign-off. Plan in `docs/plans/m1-16.md`, ADR 0015 | Plan in `docs/plans/m1-16.md`, ADR 0015 |

Suggested order by week: 3 (M1.1 to M1.3, CI Ubuntu leg), 4 (M1.4, M1.5 logic), 5 (M1.6, M1.7, first benchmarks), 6 (M1.8, M1.9), 7 (M1.10, M1.11, fault suite running), 8 (M1.12 to M1.16, soak, gate review).

### 4.2 Engine gate (end of M1), from TPL 4

- [ ] 100% of TPL 5.1 and 5.2 P1 cases pass (TC-ENG-001 to 024, TC-RES-001 to 012)
- [ ] Fault-injection suite passes 3 consecutive nightly runs
- [ ] Benchmark median within 10% of IDM
- [ ] Engine line coverage at least 80%
- [ ] No open Critical or High defect in the engine
- [ ] Gate report with build number and evidence links; plan v0.2 with M2 cases detailed

### 4.3 Dependencies to approve in M1

`System.Security.Cryptography.ProtectedData`, Dapper, FluentFTP, Serilog, FsCheck.Xunit, Testcontainers, BenchmarkDotNet, SharpFuzz. All but the first are on the CLAUDE.md stack list or named in the test plan; ProtectedData is the one genuinely new addition. Confirm each licence on NuGet when adding it and put it in the PR description and the `Directory.Packages.props` comment.

## 5. M2: Browser integration, queues, categories, main UI (weeks 9 to 13)

**Goal:** a user can click a download in Chrome, Edge or Firefox and watch Kasi finish it in a real window, with queues and categories. Ends in the internal alpha.

| # | Task | Covers | Verified by |
| --- | --- | --- | --- |
| ✅ M2.1 | **`Kasi.Ipc`**: message contracts for the TRD 9.4 protocol (`hello`, `rules`, `addDownload`, `addBatch`, `mediaDetected`, `captureMedia`, `refreshLink`, `linkRefreshed`, `openSettings`, `ack`, `error`), JSON schema in `docs/protocol/kasi-messages.schema.json`, unknown types and fields rejected, named pipe `\\.\pipe\kasi-<SID>` with a current-user-only ACL, client and server. Security review, two approvals (**the second approval was waived by the owner on 2026-10-23**: the repository has one developer, so the review is the self-review checklist on the `security` PRs #53 and #54) | NFR-010 | schema tests; pipe refuses a second user's connection |
| ✅ M2.2 | **`Kasi.NativeHost`** (Native AOT): 4-byte length-prefixed JSON on stdin and stdout, validation, relay to the pipe, starts `Kasi.exe --tray` and retries for 5 s, size limits | BRW-006, NFR-010, TC-BRW-002, 006 | 100,000 malformed messages fuzzed with no crash; AOT publish added to CI. **2.2a done:** frame writer (1 MiB cap, one lock), `HostRelay` (two pumps, error replies, exit 2 on a broken frame, one reconnect with `hello` replay, oversize replaced by `INTERNAL`), `AppLauncher` (sibling `Kasi.exe --tray`, 50 to 500 ms backoff for 5 s), manifest template and `scripts/native-host/Register-NativeHost.ps1`, ADR 0017 started; `Kasi.NativeHost.Tests` (TC-BRW-002). **2.2b done:** `NativeHostFuzzTests` (TC-BRW-006: 100,000 seeded malformed frames in one session, each answered with a fixed-text `error`, nothing forwarded, no memory growth, still serving at the end; 1,000 framing-level breaks, each exit 2 with no partial frame), `HostMessageFuzz` in `tests/Kasi.Fuzz` (5 minutes a night in `nightly-fuzz.yml` through `Run-Fuzz.ps1 -Target host`, findings replayed from `tests/fixtures/fuzz/host-crashes`), the `native-host-aot` job in `ci.yml` (zero-warning AOT publish, 5 MB limit, cold-start median in the job summary, 1,000 malformed frames against the published executable over real pipes via `build/Measure-NativeHost.ps1` and `AotExecutableTests`); measured 3.48 MB and a 22 to 24 ms cold start on the development machine. The fuzz found a real 2.2a defect (invalid UTF-8 in an `id` made `TryReadId` throw), fixed and saved as a finding. `--tray` itself arrives with M2.8. Plan in `docs/plans/m2-2.md` |
| ✅ M2.3 | **Extension scaffold** in `extension/`: TypeScript strict, Vite, webextension-polyfill, ESLint, Prettier, Vitest, Playwright; message types generated from the schema; builds for Chromium and Firefox. **2.3a implemented:** dual bundles, manifests and asset check, generated types and drift guard, entry scaffolds. **2.3b implemented:** native port manager with reconnect and hello, rules cache, redacting logger, Ajv fixture suite, Windows CI job, nightly browser smoke, Firefox host-permission step | NFR-015, NFR-010 | `npm ci; npm run check:types; npm run lint; npm run format:check; npm run build` pass locally; `npm test` runs 127 Vitest cases; `npm run smoke:chromium` and `smoke:firefox` run nightly (green on 22 and 23 Oct 2026) |
| 🔶 M2.4 (**open:** the nightly leg against the real `Kasi.App --tray` of decision 6 of the M2.8 plan; Chrome and Edge branded builds, which ignore `--load-extension`, are covered only by the manual checklist) | **Interception** (plan `docs/plans/m2-4.md`; 2.4a implemented: rule semantics, optional `minSizeBytes` on `rules` for extensions 0.2.0 and later, ADR 0019 started; 2.4b implemented: pause, cookies, the 6 s ready wait plus 2 s ack hand-off, late-ack cancel, bypass detection, exclusions, native-port fixes, unit tests `TC-BRW-001` to `004`, ADR 0019 extended; 2.4c implemented: `Kasi.BrowserHarness` stub Kasi on the real pipe, Chromium and Firefox suites for `TC-BRW-001` to `004` with the real Native AOT host, `.github/workflows/extension-e2e.yml`, `docs/qa/browser-interception.md`, ADR 0019 finished; Firefox verified locally with geckodriver 0.37.1): `downloads.onCreated`, rules pushed by Kasi, pause, `addDownload` with cookies, ack within 2 s then cancel and erase, otherwise resume in the browser; bypass key (default Alt); never `blob:`, `data:`, POST forms or incognito | BRW-002, BRW-003, TC-BRW-001 to 004 | Vitest driving Playwright (Chromium) and geckodriver (Firefox) against `Kasi.TestServer` (a process or in-process host, not a container) and a stub Kasi |
| ✅ M2.5 (2.5a, 2.5b and 2.5c implemented; the Kasi half of the refresh is M2.8d; a real right-click on a menu is the manual row in `docs/qa/browser-interception.md`) | **Status, toggle, menus, link batch** (plan `docs/plans/m2-5.md`, decisions approved): status turns red within 5 s when the host is missing or Kasi quits (the host reports a lost pipe instead of relaunching); interception toggle; context menus (Download with Kasi, all links, selected links) and the `addBatch` for the picker (**2.5c implemented**: `refreshLink` opens the referrer in a background tab and takes the first matching download through the `claims` seam, then sends `linkRefreshed`, e2e in both browsers; **2.5b implemented**: links of every frame through `scripting.executeScript`, a pure normaliser, schema-valid `addBatch` and `addDownload`, menus wait for Kasi like a download, e2e `TC-BRW-005` with 40 links and 12 selected in both browsers). The picker window and the 12-of-40 jobs are Kasi's (M2.8d); clipboard capture moves to Kasi (M2.8e, P3) | BRW-004 to 006, TC-BRW-005 (extension half) | Vitest; host tests; Playwright and geckodriver with the stub Kasi |
| 🔶 M2.6 (**open:** a run against store-signed builds with the real IDs, which needs the owner's store submissions; the installer takes over registration in M4) | **Host registration IDs and the allow-list test** (plan `docs/plans/m2-6.md`, decisions approved; the script and manifest template exist since M2.2a): per-browser store IDs in `extension/store-ids.json`, script options for several IDs and a test registry root, `TC-BRW-007` end to end in Chromium and Firefox (installer takes over in M4) | BRW-001, NFR-010, TC-BRW-007 | message from a non-allow-listed ID is refused by the browser |
| 🔶 M2.7 (**implemented in #89:** packager, listing text, justifications checked against the manifest, data-usage answers, review notes, privacy policy, generated screenshots and promo tiles, ADR 0022; **open, owner steps:** the contact address in the privacy policy and its public URL, the three store accounts, the unlisted submissions, and logging review feedback in `docs/store/review-log.md`) | **Store packages and early submission** (plan `docs/plans/m2-7.md`, decisions approved): packager, justifications that match the code, privacy policy, review notes; the owner submits as unlisted after M2.8b | BRW-001, NFR-013, risk register | package and justification tests; review feedback logged; fixes planned before the alpha |
| 🔶 M2.8 (**all five PRs implemented; open:** a FlaUI smoke suite for the dialogs, the picker, the refresh window and the tray (none exists yet, see M2.13), the browser legs against the real `Kasi.App --tray`, and a by-hand check of the tray icon and the toasts, which need the M4 installer to be complete) | **App core** (plan `docs/plans/m2-8.md`, decisions approved; five PRs, with 2.8d split into four: **2.8e implemented** (off by default; while off nothing is listened to or read; a copied single download link with a listed extension opens the pre-filled Add dialog); **2.8c implemented** (tray with total speed, Pause all, Resume all, speed presets, Open and Quit tested through a view model; the window hides on close and only Quit ends Kasi; completion and failure toasts with Open and Show in folder, routed safely); **2.8d-4 implemented** (a download paused for want of access opens the refresh dialog; Kasi asks the connected browsers to open the referrer page and resumes from the saved bytes with the new link and cookies; also a context-menu item on paused downloads); **2.8d-3 implemented** (the link picker: text and file-type filters, select shown or none, destination, one job per ticked link sharing the batch's cookies, 100,000 links in a virtualised list); **2.8d-2 implemented** (the Add dialog with every TRD 12.1 field mapped onto the job, cookies saved as the job secret, the duplicate prompt, "remember for this site", and the Settings switch for browser downloads); **2.8d-1 implemented** (the engine is hosted in the app after the pipes open, `IJobService` is the UI's facade, and the main window lists the jobs through the 250 ms batch dispatcher); **2.8b implemented**: `BrowserMessageHandler` answers `hello` with rules from `BrowserSettings` (populated defaults, `minSizeBytes` only to extensions 0.2.0 or later), acknowledges `addDownload` and `addBatch` when queued in `BrowserIntake` (idempotent request ids, bounded queues), `openSettings` opens a Settings window skeleton, a settings save pushes rules to every host, tests `BrowserHandlerTests` (TC-BRW-002, 005, 006 Kasi half), ADR 0023 extended; queued downloads wait for the Add dialog of 2.8d; **2.8a implemented**: `Program` takes the `Local\Kasi-<SID>` mutex, a second launch hands its arguments over the per-user pipe `kasi-<SID>-open` (`HandOffServer`/`HandOffClient` in `Kasi.Ipc`) and exits 0, the `KasiPipeServer` opens before the engine and UI, `AppArguments` (URLs and `--tray` only), `LaunchRelay`, `UiBatchDispatcher` with `FakeTimeProvider` tests, ADR 0023 started; tests `StartupTests` incl. `TC-SYS-006` with a real second process, `HandOffTests`, `AppArgumentsTests`, `UiBatchDispatcherTests`): pipe-first start-up and single instance with argument hand-off (2.8a); Kasi's side of the browser protocol, rules sender and Settings skeleton (2.8b); tray, close-to-tray and toasts with Open and Open folder (2.8c); Add-download dialog, duplicate prompt (TC-ENG-021 UI half), link picker and refresh-link dialog (2.8d); clipboard capture (2.8e, P3); `UiBatchDispatcher` (250 ms) | SYS-005, UI-001, UI-002, UI-004, UI-005, ENG-013, BRW-005, BRW-007, TC-UI-001, 003, 004, TC-SYS-006, TC-BRW-005 (Kasi half), 008 | headless view-model tests; integration tests; FlaUI smoke |
| 🔶 M2.9 (plan `docs/plans/m2-9.md`, all four PRs implemented: **9a implemented**: toolbar, sidebar with live counts, eight sortable columns, search, status bar, shortcuts, delete confirmation; **9b implemented**: column layout, sort order and theme override persistence; **9c implemented**: engine segment/log events and details window with live segment map; **9d implemented**: 10,000-row budget under 100 ms, 20 active jobs updating under 16 ms, keyboard navigation and automation names coverage, Mica backdrop; **open:** FlaUI smoke suite nightly in M2.13) | **Main window and job views** per `docs/wireframes/`: toolbar, category and status sidebar, queue tabs, virtualised `DataGrid`, status bar, job details with live segment map (at least twice per second), keyboard shortcuts, automation names on every control | UI-001, UI-003, UI-006 (theme follows Windows), UI-010 (names from day one), NFR-006 groundwork, TC-UI-002, 005, 006, 007 | FlaUI smoke suite nightly; 10,000-row prototype stays under 16 ms per frame |
| ⬜ M2.10 (not started; `Kasi.Scheduling` is an empty project, and nothing starts a queued job except "start now" in the Add dialog) | **`Kasi.Scheduling` core**: queue runner with parallel limit, reorder and move between queues with persistence, start and stop schedules (clock injected), missed-schedule rule, post-completion actions with the 60 s cancellable countdown | QUE-001 to 004, TC-QUE-001 to 004, 007 | unit tests with a fake `TimeProvider`; countdown tests |
| ⬜ M2.11 (not started; the default categories and folders exist, and a job takes its category from the dialog or a remembered site) | **Categories and file safety**: default categories and folders, user rules by extension, domain, URL pattern with correct precedence, Mark-of-the-Web (`Zone.Identifier`), optional AMSI scan | CAT-001 to 004, TC-CAT-001 to 004 | unit tests for rule order; EICAR flagged; `Zone.Identifier` stream present |
| 🔶 M2.12 (**delivered:** cookies go to `job_secret` with the job in M2.8d-2, are deleted on completion by the engine and are never logged; **open:** the end-to-end test that downloads a login-protected file through the extension and the real Kasi) | **Cookie and header end-to-end**: extension cookies to `job_secret`, deleted on completion, never logged | ENG-009, NFR-013 | login-protected test file downloads via the extension |
| 🔶 M2.13 (**delivered:** the extension job, the nightly extension smoke and the 5-minute nightly host fuzz; **open:** FlaUI and its nightly) | **CI**: extension job, FlaUI nightly, host fuzz in the nightly budget (5 min) | TPL 2.1 | jobs green |

### 5.1 Internal alpha gate (end of M2), from TPL 4

- [ ] All BRW, QUE, CAT and UI P1 cases pass
- [ ] Browser hand-off passes end to end in Chrome, Edge and Firefox
- [ ] FlaUI smoke suite green; no open Critical defect
- [ ] Gate report; plan v0.3

## 6. M3: Media capture and scheduler extras (weeks 14 to 17)

**Goal:** pick a quality from a playing stream and get one playable file; DRM is refused every time. No formal gate; plan v0.4 closes the phase.

| # | Task | Covers | Verified by |
| --- | --- | --- | --- |
| ⬜ M3.1 | **HLS and DASH parsers** in `Kasi.Media`: master and media playlists, `EXT-X-MEDIA` audio groups, MPD `AdaptationSet` and `Representation` with `SegmentTemplate`, `SegmentList`, `SegmentBase`; manifest size cap 10 MB; golden fixtures from public test streams only (Apple, DASH-IF) | MED-001, MED-002, TC-MED-001, 002 | golden-file tests; parser fuzz targets (5 min each nightly) |
| ⬜ M3.2 | **DRM check** before any segment request: SAMPLE-AES, SAMPLE-AES-CTR, FairPlay/Widevine/PlayReady key formats, any DRM `ContentProtection`. No setting, flag or code path to bypass. Two approvals, `security` tag | MED-005, TC-MED-005 | `/hls` and `/dash` DRM kinds refused with the exact message and **zero segment requests** at the server; an architecture test checks there is no option |
| ⬜ M3.3 | **Segment download and assembly**: parent job with segment children, up to 6 in parallel (1 to 16), per-segment completion in new `media_stream` and `media_segment` tables (migration `0003`, ADR first for the DDL), batched writes every 1 s, concatenation with fMP4 init first, optional AES-128 decrypt with an openly served key | MED-003, MED-006, TC-MED-003, 006 | kill during capture resumes without re-fetching finished segments; `/hls/aes128` output matches plaintext |
| ⬜ M3.4 | **FFmpeg wrapper**: bundled LGPL build as an external process, job object that kills it if Kasi exits, stderr into the job log, MP4 or MKV mux without re-encoding, `ffprobe` duration check within 1 s. Compliance: licence file, offer of exact source, replaceable binary, `THIRD-PARTY-NOTICES.txt` | MED-004, TC-MED-004, TRD 13.4 | output plays; duration within 1 s. **Prerequisite:** extend `Kasi.TestServer` with real muxable TS and fMP4 fixtures, because the current segments are opaque bytes |
| ⬜ M3.5 | **Extension media detection**: `webRequest.onResponseStarted` classification, segment and duplicate suppression, badge, overlay on the largest `<video>`, popup list, `mediaDetected` and `captureMedia` | MED-001, TC-MED-001 | Playwright test pages for HLS, DASH and direct media |
| ⬜ M3.6 | **Media picker UI**: title, thumbnail, quality list with size, MP4 or MKV or audio only, subtitles | MED-002, UI wireframe | headless tests; default is highest quality at or below the user's maximum (1080p) |
| ⬜ M3.7 | **P3 media:** WebVTT subtitles next to the video (MED-008, TC-MED-008); live streams without `ENDLIST` re-polled every target duration (MED-007, TC-MED-007). Do these only if capacity allows | | 10-minute live test gives a 10-minute file |
| ⬜ M3.8 | **Scheduler extras:** wake from sleep with a waitable timer and a check that wake timers are allowed (QUE-005, TC-QUE-005); data quota with daily, weekly or never reset (QUE-007, TC-QUE-006); periodic sync (QUE-006, P3, optional) | as listed | quota test; wake test on **two physical laptops** (TPL 10) |
| ⬜ M3.9 | **Plan v0.4:** detail the M4 test cases (torrent, installer, update, compatibility) | TPL 1.3 | document updated |

## 7. M4: BitTorrent, packaging, updates (weeks 18 to 21)

**Goal:** signed installer and portable builds with in-place updates, torrents in the same queue, and the public beta. Start-of-M4 items: choose the crash-reporting backend (Appendix B #6, Sentry), pick 50 or more beta users including people on metered and unstable connections.

| # | Task | Covers | Verified by |
| --- | --- | --- | --- |
| ⬜ M4.1 | **`Kasi.Torrent` adapter**: one MonoTorrent engine, magnet and `.torrent` and browser hand-off, metadata fetch with 5-minute wait, file tree dialog, `DoNotDownload` for unticked files, state mapping, seeding at ratio 1.0 or 24 h that does not count toward the queue limit, fast-resume in `torrent_state` | TOR-001 to 004, TC-TOR-001 to 004 | opentracker plus three MonoTorrent seeders in Testcontainers (Ubuntu CI job); trackerless magnet finds peers through DHT in the staging swarm |
| ⬜ M4.2 | **Shared bandwidth:** MonoTorrent rate limiters replaced by adapters on the global token bucket | TOR-005, TC-TOR-005 | 1 MB/s cap holds within 5% across two HTTP jobs and one torrent |
| ⬜ M4.3 | **File associations** for `magnet:` and `.torrent` under HKCU with consent, then open Default Apps | TOR-006, TC-TOR-006 | registered per user; opens Kasi |
| ⬜ M4.4 | **Speed profiles on a schedule**, tray presets | BWL-001, BWL-003, TC-BWL-001, 002 | profile switches at the set time; tray change applies within 1 s |
| ⬜ M4.5 | **Velopack packaging**: per-user installer without admin (under 60 MB with FFmpeg), portable zip with `portable.marker` and `.\data`, x64 and ARM64, host manifests in HKCU, AUMID, start menu, uninstall entry that asks to keep history, optional startup with Windows, store page for each detected browser | SYS-001, SYS-003, NFR-020, TC-SYS-001, 002, 004 | install and uninstall on a clean VM leaves no host manifests; portable writes no registry keys until browser integration is on |
| ⬜ M4.6 | **Signing and release pipeline:** EV certificate or Azure Trusted Signing, timestamps, signed installer and update packages, signed beta build published to the `beta` channel on every merge to `main` (TRD 14.5 step 2), static feed on object storage behind a CDN. Pipeline changes need the lead developer or CTO | NFR-012 | signed artefacts verified; tampered package rejected |
| ⬜ M4.7 | **Self-update:** daily check, delta download with the engine at low priority, Velopack verification, apply on exit or "Restart to update" after a checkpoint, `stable` and `beta` channels | SYS-002, TC-SYS-003 | update from the previous beta build verified; delta package used |
| ⬜ M4.8 | **Settings export and import** of settings, categories and job list | SYS-004, TC-SYS-005 | export on one machine restores on another |
| ⬜ M4.9 | **Opt-in crash reporting** with scrubbing of URLs, paths, usernames and cookies; privacy verification | SYS-006, NFR-013, TC-SYS-007 | payload contains no URLs or paths; a fresh install's network capture shows only the update check |
| ⬜ M4.10 | **Third-party notices** under Settings, About, and `THIRD-PARTY-NOTICES.txt` | TRD 13.4 | file generated in CI and checked |
| ⬜ M4.11 | **Compatibility smoke run:** Windows 10 22H2, Windows 11 23H2, 24H2, 25H2 (x64), Windows 11 ARM64; Chrome and Edge latest three, Firefox latest and ESR | NFR-014, NFR-015 | smoke suite green on the VM matrix; ARM64 runner in CI |
| ⬜ M4.12 | **Beta programme:** 50 or more users, feedback channel, defect triage cadence, opt-in crash data starts accruing | NFR-008 | at least 7 days of beta before M5 closes |

### 7.1 Public beta gate (end of M4), from TPL 4

- [ ] All P1 cases pass; at least 90% of P2 cases pass, deferrals approved
- [ ] Update from the previous beta build verified; DRM refusal cases pass
- [ ] Compatibility matrix smoke run green; signed installer and portable builds produced
- [ ] No open Critical or High defect
- [ ] Gate report; plan v0.5

## 8. M5: Polish, accessibility, localisation, release (weeks 22 to 24)

**Goal:** meet every NFR target in a signed build and ship v1.0. Entry: the beta ran at least 7 days with 50 or more users.

| # | Task | Covers | Verified by |
| --- | --- | --- | --- |
| ⬜ M5.1 | **Grid polish:** configurable and persisted columns and sort, search and filter within 100 ms for 10,000 jobs | UI-007, UI-008, NFR-006, TC-UI-006 | frames under 16 ms with 20 jobs updating |
| ⬜ M5.2 | **Themes:** manual light, dark and system override | UI-006, TC-UI-005 | override persists |
| ⬜ M5.3 | **Accessibility audit:** Narrator reads name, status and progress; full keyboard operation; WCAG 2.2 AA contrast; Accessibility Insights scan clean | UI-010, NFR-017, TC-UI-007 | audit report |
| ⬜ M5.4 | **English only (Appendix B #7):** Kiswahili is not shipped. Keep every user-visible string in `.resx` so a language can be added later, and audit that nothing is hard-coded | UI-009 dropped, TC-UI-008 not run | no hard-coded strings; the requirement change is recorded in the TRD and TPL |
| ⬜ M5.5 | **Performance sign-off:** cold start under 1.5 s, idle memory under 80 MB, under 250 MB with 20 jobs, CPU under 5% of one core at 100 MB/s, installer under 60 MB, final IDM comparison within 5% | NFR-001 to 006, NFR-020 | benchmark report with PerfView, ETW and Windows Performance Recorder evidence |
| ⬜ M5.6 | **Full regression and compatibility matrix**; final fault-injection run | NFR-007, NFR-014, NFR-015 | all suites green on the release candidate |
| ⬜ M5.7 | **Usability sessions:** first download from install in under 2 minutes for a new user (5 moderated users) | NFR-016 | session report |
| ⬜ M5.8 | **Beta feedback fixes**; crash-free sessions at least 99.5% over 30 days of opt-in data | NFR-008 | telemetry report |
| ⬜ M5.9 | **Store approval** in the Chrome Web Store, Edge Add-ons and AMO | BRW-001 | listings approved |
| ⬜ M5.10 | **Release:** tag `v1.0.0`, signed stable build, release notes from merged PRs, artefacts to the feed and download page, extension packages submitted, licence and source-availability decisions applied | TRD 14.5 step 3 | release sign-off by CTO and QA lead |

### 8.1 v1.0 release gate (end of M5), from TPL 4

- [ ] All P1 cases pass; P2 deferrals approved
- [ ] All KASI-NFR targets met
- [ ] Crash-free sessions at least 99.5%
- [ ] Full compatibility matrix passed
- [ ] Extension approved in all three stores
- [ ] Release test summary report; plan v1.0; sign-off by CTO and QA lead

## 9. Decisions and external items

| # | Item | Owner | Needed by |
| --- | --- | --- | --- |
| 1 | Product name: Kasi Download Manager | Martin Maiyo | done |
| 2 | Licensing and business model: Free and Open Source, MIT licence | MartSoft Solutions | done |
| 3 | UI library: decided 17 Oct 2026 (Avalonia + FluentAvalonia, MIT only) | lead developer | done |
| 4 | Code-signing certificate | Martin Maiyo | end of M4 |
| 5 | Source availability and licence: open source under MIT; `LICENSE` added and the repo is public. MIT is compatible with the MIT and Apache-2.0 dependencies and with FFmpeg shipped as a separate LGPL executable | MartSoft Solutions | done |
| 6 | Crash-reporting backend (Sentry, hosted or self-hosted) | lead developer | start of M4 |
| 7 | Kiswahili localisation: keep English only | Martin Maiyo | done |
| 8 | Appoint QA lead | Martin Maiyo | start of M1 |
| 9 | Benchmark machine and IDM test licence | Martin Maiyo | week 4 |
| 10 | ARM64 runner: GitHub-hosted `windows-11-arm` | lead developer | done |
| 11 | Beta users (50 or more) | Martin Maiyo | start of M4 |

## 10. Risks to watch

From TRD Appendix C and TPL 10, with the action each needs in this plan.

| Risk | Where it bites | Action |
| --- | --- | --- |
| Throughput falls short of IDM | M1 gate | weekly benchmarks from week 5; time-boxed tuning sprint before M2 |
| Chrome Web Store rejects or delays the extension | M2, M4, M5 | early submission in M2.7; clear permission justifications |
| Manifest V3 limits `webRequest` further | M3 | detection stays non-blocking; fall back to `downloads` API and `<video>` inspection |
| SmartScreen warnings and antivirus false positives | M4 | sign every build from the first public one; submit builds to Microsoft and major AV vendors |
| Servers ban aggressive clients | M1 | gradual ramp-up, per-host caps, honour `429` and `Retry-After` |
| Power-cut VM tests miss real disk-cache bugs | M1 onward | monthly manual run on a physical machine with a consumer SSD |
| Wake timers differ across laptops | M3 | two physical devices; warn when wake timers are disabled |
| Flaky network tests hide real failures | all | seeded chaos; quarantine within one working day, fix or delete within one sprint |
| IDM comparison noise from public mirrors | M1, M5 | alternating runs, medians, two time windows, a capped self-hosted origin as control |
| Linux containers cannot run on GitHub Windows runners | M1 | Ubuntu CI job for Testcontainers (M1.16) |

## 11. Not in v1.0

Recorded so scope stays honest: macOS and Linux builds, mobile, cloud sync, site-specific extractors, any handling of DRM content, a local REST API (KASI-SYS-008, P4), and a command-line interface (KASI-SYS-007, P4). P3 items (BRW-007, MED-007, MED-008, QUE-006) ship only if capacity allows; UI-009 (Kiswahili) is dropped, v1.0 is English only; the rest of the P2 list can slip to v1.1 with approval.

## 12. Requirement coverage

Every requirement from TRD sections 3 and 4 and the phase that delivers it. Priorities are P1 to P4 (TRD 1.4). "Engine half" and "extension half" mean the work is split across phases.

### 12.1 Download engine (ENG)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| ENG-001 HTTP/1.1, HTTP/2, HTTPS | P1 | M1 | M1.7 |
| ENG-002 FTP and FTPS | P2 | M1 | M1.12 |
| ENG-003 Probe, filename | P1 | M1 | M1.3, M1.4 |
| ENG-004 1 to 32 connections | P1 | M1 | M1.5 |
| ENG-005 Dynamic segmentation | P1 | M1 | M1.5 |
| ENG-006 Connection reuse | P1 | M1 | M1.7 |
| ENG-007 Range-less fallback | P1 | M1 | M1.4 |
| ENG-008 Single `.kasipart` file | P1 | M1 | M1.6 |
| ENG-009 Authenticated downloads | P1 | M1, M2 | M1.10, M2.12 |
| ENG-010 Redirects, refresh link | P1 | M1, M2 | M1.10, M2.8 |
| ENG-011 Proxies | P2 | M1 | M1.12 |
| ENG-012 Checksum verification | P2 | M1 | M1.12 |
| ENG-013 Duplicate detection | P1 | M1, M2 | M1.12, M2.8 |

### 12.2 Resume and recovery (RES)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| RES-001 Persist every 2 s | P1 | M1 | M1.2, M1.9 |
| RES-002 Resume from exact byte | P1 | M1 | M1.9, M1.14 |
| RES-003 Validator check | P1 | M1 | M1.9 |
| RES-004 Retry with backoff | P1 | M1 | M1.8 |
| RES-005 Non-resumable warning | P1 | M1 | M1.4 |
| RES-006 Crash-safe job list | P1 | M0, M1 | M0 step 3, M1.2, M1.14 |

### 12.3 Browser integration (BRW)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| BRW-001 Store listings | P1 | M2 submit, M5 approved | M2.7, M5.9 |
| BRW-002 Intercept and hand off | P1 | M2 | M2.4 |
| BRW-003 Bypass key | P1 | M2 | M2.4 |
| BRW-004 Context menu | P2 | M2 | M2.5 |
| BRW-005 Link picker | P2 | M2 | M2.5 |
| BRW-006 Connection status | P1 | M2 | M2.2, M2.5 |
| BRW-007 Clipboard capture | P3 | M2 if capacity | M2.5 |

### 12.4 Media capture (MED)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| MED-001 Detect streams | P1 | M3 | M3.1, M3.5 |
| MED-002 Quality list | P1 | M3 | M3.1, M3.6 |
| MED-003 Parallel, resumable segments | P1 | M3 | M3.3 |
| MED-004 Mux to MP4 or MKV | P1 | M3 | M3.4 |
| MED-005 DRM refusal | P1 | M3 | M3.2 |
| MED-006 AES-128 | P2 | M3 | M3.3 |
| MED-007 Live streams | P3 | M3 if capacity | M3.7 |
| MED-008 WebVTT subtitles | P3 | M3 if capacity | M3.7 |

### 12.5 BitTorrent (TOR), all P2

| ID | Phase | Task |
| --- | --- | --- |
| TOR-001 Magnet, file, hand-off | M4 | M4.1 |
| TOR-002 File selection | M4 | M4.1 |
| TOR-003 DHT, PEX, LPD, uTP, encryption | M4 | M4.1 |
| TOR-004 Seeding limits | M4 | M4.1 |
| TOR-005 Shared bandwidth cap | M4 | M4.2 |
| TOR-006 `magnet:` and `.torrent` handler | M4 | M4.3 |

### 12.6 Queues and scheduler (QUE)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| QUE-001 Queues with parallel limit | P1 | M2 | M2.10 |
| QUE-002 Reorder and move | P1 | M2 | M2.10 |
| QUE-003 Start and stop schedule | P1 | M2 | M2.10 |
| QUE-004 Post-completion action | P1 | M2 | M2.10 |
| QUE-005 Wake from sleep | P2 | M3 | M3.8 |
| QUE-006 Periodic sync | P3 | M3 if capacity | M3.8 |
| QUE-007 Data quota | P2 | M3 | M3.8 |

### 12.7 Categories (CAT)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| CAT-001 Default categories | P1 | M2 | M2.11 |
| CAT-002 User rules | P1 | M2 | M2.11 |
| CAT-003 Mark-of-the-Web | P1 | M2 | M2.11 |
| CAT-004 AMSI scan | P2 | M2 | M2.11 |

### 12.8 Bandwidth (BWL)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| BWL-001 Global limit, tray | P1 | M1 engine, M2 tray, M4 presets | M1.11, M2.8, M4.4 |
| BWL-002 Per-job limit | P1 | M1 | M1.11 |
| BWL-003 Scheduled profile | P2 | M4 | M4.4 |

### 12.9 User interface (UI)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| UI-001 Main window | P1 | M2 | M2.9 |
| UI-002 Add dialog | P1 | M2 | M2.8 |
| UI-003 Segment map | P1 | M2 | M2.9 |
| UI-004 Tray | P1 | M2 | M2.8 |
| UI-005 Toasts | P1 | M2 | M2.8 |
| UI-006 Light and dark | P2 | M2 follows Windows, M5 override | M2.9, M5.2 |
| UI-007 Columns and sort | P2 | M5 | M5.1 |
| UI-008 Search and filter | P2 | M5 | M5.1 |
| UI-009 Localisation (Kiswahili) | P3 | dropped from v1.0: English only | M5.4 keeps strings in resources |
| UI-010 Keyboard and screen reader | P2 | M2 names, M5 audit | M2.9, M5.3 |

### 12.10 System (SYS)

| ID | Pri | Phase | Task |
| --- | --- | --- | --- |
| SYS-001 Installer (P1), portable (P2) | P1/P2 | M4 | M4.5 |
| SYS-002 Self-update | P1 | M4 | M4.7 |
| SYS-003 Start with Windows | P1 | M4 | M4.5 |
| SYS-004 Export and import | P2 | M4 | M4.8 |
| SYS-005 Single instance | P1 | M2 | M2.8 |
| SYS-006 Crash reporting | P2 | M4 | M4.9 |
| SYS-007 Command line | P4 | backlog | none |
| SYS-008 REST API | P4 | backlog | none |

### 12.11 Non-functional (NFR)

| ID | Pri | Where it is proven |
| --- | --- | --- |
| NFR-001 Throughput within 5% of IDM | P1 | M1.15 (gate 10%), M5.5 (release 5%) |
| NFR-002 1 Gbit/s on 16 connections | P1 | M1.15 |
| NFR-003 Start under 1.5 s | P1 | measured from M2, signed off M5.5 |
| NFR-004 Memory under 80 MB idle, 250 MB with 20 jobs | P1 | measured from M2, signed off M5.5 |
| NFR-005 CPU under 5% at 100 MB/s | P2 | M5.5 |
| NFR-006 Grid under 16 ms with 10,000 rows | P1 | M2.9 prototype, M5.1 |
| NFR-007 No data loss | P1 | M1.14, M5.6 |
| NFR-008 Crash-free 99.5% | P1 | M4.12, M5.8 |
| NFR-009 Database survives power loss | P1 | M0 step 3, M1.14 |
| NFR-010 Validated messages, fuzzed host | P1 | M2.1, M2.2 |
| NFR-011 TLS validation always on | P1 | M1.7 |
| NFR-012 Signed, verified updates | P1 | M4.6, M4.7 |
| NFR-013 No telemetry without opt-in | P1 | M1.1 redaction, M4.9 verification |
| NFR-014 Windows 10 22H2 and 11, x64 and ARM64 | P1 | M4.11, M5.6 |
| NFR-015 Chrome, Edge, Firefox versions | P1 | M2.3, M4.11, M5.6 |
| NFR-016 First download under 2 minutes | P2 | M5.7 |
| NFR-017 WCAG 2.2 AA | P2 | M5.3 |
| NFR-018 Engine coverage at least 80% | P1 | M1.16 |
| NFR-019 Engine has no UI dependency | P1 | **done** in M0 step 2 |
| NFR-020 Installer under 60 MB | P2 | M4.5, M5.5 |

## 13. How to keep this file useful

- Mark a task ✅ only when its "verified by" evidence exists, and link the PR or gate report next to it. Update the Status column in section 1 when a phase's count changes.
- Update `README.md` (status, layout, commands) in the same PR as the change that makes it stale, as `plan.md` is. A task is not done while either still describes the old state.
- When a phase starts, split its tasks into PR-sized steps and put the TC IDs in each commit body.
- At each gate, copy the exit criteria into the gate report and attach the last three nightly fault-injection reports.
- Record each architecture decision as an ADR (`docs/adr/NNNN-title.md`). ADRs already planned: media tables DDL (M3.3), HTTP/2 and TLS in the test server (M1.7), IPC pipe and message versioning (M2.1), update channels (M4.7).
