# 0013. Fault-injection harness and nightly reporting

- Status: Accepted incrementally for M1.14 (14a landed first; 14b to 14d extend it)
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

## Consequences

- A green nightly means every kill left a consistent database and a bounded refetch, and every finished file matched its hash.
- The hook adds one null check per checkpoint commit.
- No new NuGet dependency.
