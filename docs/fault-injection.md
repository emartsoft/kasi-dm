# Fault-injection harness

`Kasi.FaultInjection` runs the fault suites of the test plan (TPL 7). It is also the child process the suites start: a
download through `DownloadEngine` that the harness kills. A run writes `fault-report.json` and `summary.md`; a failure prints
the seed and the command that replays the same schedule. Design: [ADR 0013](adr/0013-fault-injection-harness.md).

## Run it

```powershell
# 200 process kills on 4 GiB downloads, 16 connections, capped at 16 MiB/s (the nightly run)
dotnet run --project tests/Kasi.FaultInjection -c Release -- --scenario kill --report artifacts/kill/fault-report.json

# 100 kills inside a checkpoint commit (TC-RES-011)
dotnet run --project tests/Kasi.FaultInjection -c Release -- --scenario commit-kill --report artifacts/commit-kill/fault-report.json

# 50 jobs whose file changes between sessions or mid-session (TC-RES-005, TC-RES-006)
dotnet run --project tests/Kasi.FaultInjection -c Release -- --scenario server-mutation --report artifacts/server-mutation/fault-report.json

# 10 rounds of 5 s write stalls with a 1 s read-stall timeout
dotnet run --project tests/Kasi.FaultInjection -c Release -- --scenario storage-stall --report artifacts/storage-stall/fault-report.json

# 20 rounds on a full 2 GiB VHDX (run elevated: it mounts a volume)
dotnet run --project tests/Kasi.FaultInjection -c Release -- --scenario disk-pressure --report artifacts/disk-pressure/fault-report.json

# 50 jobs through Toxiproxy over up to 2 hours (needs a daemon: see below)
dotnet run --project tests/Kasi.FaultInjection -c Release -- --scenario network-soak --toxiproxy 127.0.0.1:8474 --report artifacts/network-soak/fault-report.json

# a short local run
dotnet run --project tests/Kasi.FaultInjection -c Release -- --iterations 10 --size 67108864 --rate 8388608 --max-kill-seconds 3
```

Options: `--scenario`, `--iterations`, `--seed`, `--size`, `--connections`, `--rate`, `--max-kill-seconds`, `--vhd-mib`, `--free-after-seconds`, `--toxiproxy`, `--duration-seconds`, `--parallel`, `--report`. Size, rate and connections default per suite (see `--help`). The suites
need about 4 GiB of free disk space at the default size. The process exits non-zero when any iteration fails.

## What a pass means

After every kill the database opens, `PRAGMA integrity_check` returns `ok`, and `job.done_bytes` equals the sum of the
segments' `written` bytes. The data the next run had to fetch again stays within two seconds of the throttle rate plus
1 MiB per connection. Every finished download matches the SHA-256 of the generated content.

## The other suites

- **server-mutation:** each iteration is its own job on `/mutable/{id}` and the file changes once or twice, between two sessions (the child is stopped, the file changes, a new child resumes), in the middle of a session, or both. The engine restarts a changed file at most twice. A change between sessions must always be detected; a change in the middle of a session is seen only by the next request, so when every lease was already open the file is simply the old version. In every case the finished file must be exactly one complete version, the one the job last restarted on.
- **storage-stall:** the child delays two part-file writes by 5 s with a 1 s read-stall timeout. The stall guard watches only the network read, so the download must finish with no aborted request and no byte fetched twice.
- **disk-pressure:** the download goes to a VHDX mounted in a folder that has half the free space the file needs. The job must pause with `NotEnoughSpace`, leave the database consistent, and finish after the filler file is deleted (30 s later by default). Mounting needs Administrator rights; without them the report says **SKIPPED** and the exit code is 0. A skip is not a pass.

- **network-soak:** every job reaches the test server through its own Toxiproxy proxy and meets a schedule drawn from the seed: latency of 100 to 500 ms with jitter for the whole run, a 20 s window at 64 KiB/s, a connection reset and a 30 s partition. Jobs start spread over `--duration-seconds` (default 2 h), `--parallel` (default 10) at a time. A child that ends without a finished job is started again, as a user would resume it, up to five times. Pass: every job finishes with the right SHA-256 and none sits in `Downloading` with no progress for two minutes. Without a daemon the report says **SKIPPED**.

### Running a Toxiproxy daemon

Toxiproxy (MIT) is a separate server, not a dependency of the solution. Download the release for your platform from the project's GitHub releases, check its SHA-256 against the release's checksum file, and start it (`toxiproxy-server`, REST API on `127.0.0.1:8474`). The nightly workflow does the same with a pinned version. `ToxiproxyClient` is plain `HttpClient` calls and its unit tests need no daemon.

## Power cut (Hyper-V host, owner evidence)

`scripts/fault-injection/Invoke-HyperVPowerCut.ps1` pulls the plug on a guest 20 times (TC-RES-003). Per iteration it restores a
checkpoint, starts the guest, copies the harness in, starts `--scenario power-guest --phase run` (a test server on a fixed port plus
the engine, state in `C:\kasi\state`), waits until the job has checkpointed a seeded 10 to 70 percent (or, with `-CutAtCommit`, until
the child is stuck inside a checkpoint commit), runs `Stop-VM -TurnOff`, starts the guest and runs `--phase verify`: `PRAGMA
integrity_check`, the database agreeing with itself, then the download finished with the right SHA-256.

One-time set-up of the host and the guest:

1. Hyper-V on a dedicated Windows host; run the script and the runner elevated.
2. A Windows guest with the .NET 10 runtime (the harness is copied in per iteration) and a local account for PowerShell Direct. Its password is given
   to the script only through `KASI_FAULT_GUEST_PASSWORD` (a repository secret for the workflow).
3. A **standard checkpoint** of the powered-off guest named `kasi-fault-base` (or `-Checkpoint`). Every iteration starts from it, so the run is independent of the previous cut.
4. For the workflow: a self-hosted runner labelled `self-hosted, kasi-hyperv`, the repository variables `KASI_FAULT_VM` and
   `KASI_FAULT_GUEST_USER`, and the secret `KASI_FAULT_GUEST_PASSWORD`. `power-cut.yml` runs on `main` by hand or weekly and never on a pull request.

`TC-RES-002` (a clean pause, exit and reboot, then resume) is covered by the engine's pause/resume tests and is part of the weekly checklist below, not scripted.

### Sleep and hibernate (manual, weekly, 5 iterations)

Start a download of a few GiB, let it reach about 40 percent, sleep the machine for two minutes, wake it and watch the job continue (or be resumed). Repeat with hibernate and with
a restart. Afterwards check that the finished file matches its published SHA-256 and that the job list shows no error. Record the date, the Windows build and the result in the weekly log.

## Nightly workflow

`.github/workflows/nightly-faults.yml` runs at 01:00 UTC and on demand, one matrix leg per suite (kill, commit-kill, server-mutation,
storage-stall, disk-pressure, network-soak), each with `--seed` set to the run number, and publishes `fault-report.json` and `summary.md` as the artifact
`fault-<suite>` and in the job summary. The engine gate asks for three green nights in a row (M1.16).

## Fuzzing and the gate check

`build/Run-Fuzz.ps1 -LibFuzzer <libfuzzer-dotnet-windows.exe>` fuzzes the filename parser for 3 minutes (`-Seconds`); with
`-Target host` it fuzzes the native messaging host's frame reader, strict parser and error replies (TC-BRW-006) for 5 minutes. The
nightly workflow `nightly-fuzz.yml` runs both on `main`. A finding is minimised into `artifacts/fuzz/findings` (host:
`artifacts/fuzz-host/findings`); reproduce it with `dotnet artifacts/fuzz/bin/Kasi.Fuzz.dll --replay <file>` (host: set
`KASI_FUZZ_TARGET=host` first), fix the code, and commit the file to `tests/fixtures/fuzz/crashes` (host:
`tests/fixtures/fuzz/host-crashes`).

`build/Check-M1Gate.ps1` (or the manual `m1-gate.yml` workflow) reports where the M1 engine gate stands: tagged P1 cases and a green
CI run, three scheduled nights with every suite PASS (a SKIPPED suite fails the night), the IDM scorecard, engine coverage, open
Critical or High defects, and the evidence only people can produce (the Hyper-V power cut) in `docs/m1-gate-evidence.json`.

## Fast versions in pull requests

`Kasi.FaultInjection.Tests` runs the same code with three kills on 16 MiB downloads (`KillLoopSmokeTests`) and small runs of the other suites (`FaultSuiteSmokeTests`); the disk-pressure test skips itself unless the process is elevated, and the soak test unless `KASI_TOXIPROXY` points at a daemon.

## The refetch bound

After every kill the harness works out how many bytes had to be fetched again (what the server sent minus what the job had checkpointed) and compares it with a bound.

- **Strict bound** (`StrictRefetchBound`): 2 s of data (the 1.5 s checkpoint interval plus 0.5 s of write-buffer age, KASI-NFR-007) plus 1 MiB of socket buffer per connection. A power cut is instant, so this is the bound for gate evidence on a dedicated machine (TC-RES-003).
- **Bound for a kill the harness delivers** (`RefetchBound`): the strict bound plus the data the child downloaded while the kill was landing plus 0.5 s for scheduler jitter. The kill latency is measured per iteration (from the commit hook firing, for `commit-kill`, until the child is gone) and written to `fault-report.json` as `killLatencySeconds`. On a quiet machine it is tens of milliseconds; on a loaded shared runner it reached about two seconds, which is what made the old fixed bound fail while the engine met its promise.

A resume that starts over still fails either bound by gigabytes once the download is past its first seconds.
