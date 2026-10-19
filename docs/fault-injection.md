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

# a short local run
dotnet run --project tests/Kasi.FaultInjection -c Release -- --iterations 10 --size 67108864 --rate 8388608 --max-kill-seconds 3
```

Options: `--scenario`, `--iterations`, `--seed`, `--size`, `--connections`, `--rate`, `--max-kill-seconds`, `--vhd-mib`, `--free-after-seconds`, `--report`. Size, rate and connections default per suite (see `--help`). The suites
need about 4 GiB of free disk space at the default size. The process exits non-zero when any iteration fails.

## What a pass means

After every kill the database opens, `PRAGMA integrity_check` returns `ok`, and `job.done_bytes` equals the sum of the
segments' `written` bytes. The data the next run had to fetch again stays within two seconds of the throttle rate plus
1 MiB per connection. Every finished download matches the SHA-256 of the generated content.

## The other suites

- **server-mutation:** each iteration is its own job on `/mutable/{id}` and the file changes once or twice, between two sessions (the child is stopped, the file changes, a new child resumes), in the middle of a session, or both. The engine restarts a changed file at most twice. A change between sessions must always be detected; a change in the middle of a session is seen only by the next request, so when every lease was already open the file is simply the old version. In every case the finished file must be exactly one complete version, the one the job last restarted on.
- **storage-stall:** the child delays two part-file writes by 5 s with a 1 s read-stall timeout. The stall guard watches only the network read, so the download must finish with no aborted request and no byte fetched twice.
- **disk-pressure:** the download goes to a VHDX mounted in a folder that has half the free space the file needs. The job must pause with `NotEnoughSpace`, leave the database consistent, and finish after the filler file is deleted (30 s later by default). Mounting needs Administrator rights; without them the report says **SKIPPED** and the exit code is 0. A skip is not a pass.

## Fast versions in pull requests

`Kasi.FaultInjection.Tests` runs the same code with three kills on 16 MiB downloads (`KillLoopSmokeTests`) and small runs of the other suites (`FaultSuiteSmokeTests`); the disk-pressure test skips itself unless the process is elevated.
