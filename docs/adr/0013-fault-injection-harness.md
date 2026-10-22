# 0013. Fault-injection harness and nightly reporting

- Status: Accepted incrementally for M1.14 (landed in four steps: 14a to 14d)
- Date: 2026-10-19
- Requirements: KASI-RES-001 to KASI-RES-006, KASI-NFR-007, KASI-NFR-009
- Related: ADR 0012, TPL 7

## Context

M1.13c added a child process that runs a download through `DownloadEngine` and a smoke test that kills it three times. M1.14
turns that into the repeatable suites of TPL 7: many kills, a kill inside a checkpoint commit, server mutation, disk pressure,
storage stalls, network chaos and power cuts, each with a seed and a machine-readable result for the nightly job.

## Decision (14a)

**One executable, two modes.** `Kasi.FaultInjection` keeps the child mode `<database> <job id> <bytes/s>` and adds the harness
mode (`--scenario`, `--seed`, `--iterations`, `--report`). The smoke tests moved to `Kasi.FaultInjection.Tests` and call the
same runner with a few iterations, so there is no second child and no second kill loop.

**One iteration is one kill.** The harness starts the child on a throttled download, kills the process tree after a seeded
delay (1 to 30 s), and checks the database (`PRAGMA integrity_check`, and `job.done_bytes` equal to the sum of the segments'
`written`). When a download finishes it is verified against the generated content's SHA-256 and replaced by a new one, so a
run keeps killing until the requested count is reached. A download that ends is not a kill and is not counted as one.

**Refetch bound.** The server's `BytesServed` for the iteration minus the checkpointed progress gained is the data a resume had
to fetch again. It must stay within `2 s × rate` (the checkpoint interval, KASI-RES-001) plus 1 MiB per connection for socket
buffers: the server counts bytes it handed to the kernel, which the throttled child had not read yet. A resume that starts over
fails this by gigabytes once the download is past its first seconds.

**Commit kill (TC-RES-011).** `CheckpointWriter.BeforeCommit` is an `internal` test seam (null in production, visible to the
harness through `InternalsVisibleTo`) that runs inside the open transaction. The child, armed by two environment variables,
lets a seeded number of commits through, then creates a marker file and blocks inside the next one; the harness kills it when
the marker appears. The transaction never commits, so the database must show the last committed state and nothing in between.

**Seeds reproduce the schedule, not the outcome.** The seed fixes the kill delays and the commit counts; timing and the OS still
vary. A failure prints the seed and the command that replays the schedule, and the report carries both.

**Defaults belong to the suite.** `--iterations`, `--size` and `--rate` are unset until a suite applies its default (200 kills,
100 commit kills, 4 GiB at 16 MiB/s), so a value the caller passes is never replaced.

## Decision (14b)

**Server mutation.** One job per iteration on `/mutable/{id}`, with a seeded shape: a change between sessions, in the middle of a
session, or both (two changes, the engine's cap). A change between sessions meets a fresh probe and must be detected. A change
in the middle of a session is seen only by the next lease request, so when every lease was already open the finished file is
the old version, whole. The pass rule is therefore not "every change detected" but "never a mix": the finished file equals the
content version the job last restarted on (`1 + RestartCount`), and a between-session change always counts.

**Storage stall.** The child delays two part-file writes by 5 s (`WriteFaultInjector`) with a 1 s read-stall timeout and one
connection. A guard that fires during a write shows as bytes served twice, so the suite fails on bytes served beyond the file
size as well as on aborted requests. Removing the `guard.Pause()` calls from the read path makes it fail.

**Disk pressure.** A 2 GiB expandable VHDX mounted in a folder, with a filler file that leaves half of what the download needs.
The job must be Paused with `NotEnoughSpace`, the database must pass `integrity_check`, and a resume after the filler is
deleted must finish with the right SHA-256. It needs Administrator rights; without them the report is marked skipped (exit
code 0, but not a pass) so the nightly summary shows it.

**Child hooks.** The child reads its test hooks from environment variables (`ChildEnvironment`); the command line stays
`<database> <job id> <bytes/s>`.

## Decision (14c)

**Toxiproxy without a package.** `ToxiproxyClient` is about a hundred lines of `HttpClient` calls against the daemon's REST API
(create and delete proxies, add and remove toxics, reset). Its unit tests use a fake `HttpMessageHandler`, so pull-request CI
never needs the daemon. The daemon itself (MIT) is a separate download that the nightly workflow pins and checks by SHA-256.

**One proxy per job.** Each soak job gets its own proxy on a free port and its own toxics, so a partition hits one job, and the
schedule (start offset, latency, slow window, reset, partition) is drawn from the seed before any job starts.

**A stuck job is a failure.** The harness samples each job's `done_bytes` every two seconds; a job in `Downloading` with no
progress for two minutes fails the run (the longest toxic is 30 s). A child that exits without a finished job is started again,
up to five times; needing a restart is reported, not hidden.

## Decision (flush latency in the refetch bound)

**The bound includes the slowest checkpoint commit.** A commit that runs slow on a shared runner postpones the checkpoint by that
long, so a kill loses more than the 2 s the strict bound allows. The child reports its slowest commit through a test-only
`CheckpointWriter.CommitCompleted` seam; the harness adds it to the bound (`RefetchBound`) and records it per iteration as
`flushLatencySeconds`. The strict bound, the power-cut evidence and TC-RES-003 are unchanged, and the jitter constant was not widened.

## Decision (14d)

**The guest half is part of the harness.** `--scenario power-guest` has three phases (`run`, `progress`, `verify`). Its test server uses a
fixed port and the content is generated, so after a power cut the state folder is all a restarted guest needs. `verify` writes
`power-report.json`. The phases run on a developer machine too: a unit test kills the guest process instead of the power.

**The host half is one script.** `Invoke-HyperVPowerCut.ps1` restores a checkpoint for every iteration, so no cut depends on the previous one, and
cuts with `Stop-VM -TurnOff`. It cuts at a seeded percentage or, with `-CutAtCommit`, while the guest is stuck inside a checkpoint
commit (the hook of 14a, armed by the same environment variables).

**Where each suite runs.** The hosted nightly workflow has one matrix leg per suite, so no job nears the six-hour limit.
Toxiproxy is downloaded at a pinned version and checked by SHA-256 before it starts. The Hyper-V run is a separate workflow for a
self-hosted runner, restricted to `main`, `schedule` and `workflow_dispatch`; a self-hosted runner on a public repository must never run
a pull request's code. Those runs, and the weekly sleep/hibernate checklist, are owner evidence.

## Consequences

- A green nightly means every kill left a consistent database and a bounded refetch, and every finished file matched its hash.
- The hook adds one null check per checkpoint commit.
- No new NuGet dependency.
