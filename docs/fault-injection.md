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

# a short local run
dotnet run --project tests/Kasi.FaultInjection -c Release -- --iterations 10 --size 67108864 --rate 8388608 --max-kill-seconds 3
```

Options: `--scenario`, `--iterations`, `--seed`, `--size`, `--connections`, `--rate`, `--max-kill-seconds`, `--report`. The suites
need about 4 GiB of free disk space at the default size. The process exits non-zero when any iteration fails.

## What a pass means

After every kill the database opens, `PRAGMA integrity_check` returns `ok`, and `job.done_bytes` equals the sum of the
segments' `written` bytes. The data the next run had to fetch again stays within two seconds of the throttle rate plus
1 MiB per connection. Every finished download matches the SHA-256 of the generated content.

## Fast versions in pull requests

`Kasi.FaultInjection.Tests` runs the same code with three kills on 16 MiB downloads: `KillLoopSmokeTests`.
