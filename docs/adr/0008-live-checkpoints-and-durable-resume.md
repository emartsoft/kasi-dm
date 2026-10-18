# 0008. Live checkpoints, durable resume metadata and restart on a changed file

- Status: Accepted for M1.9
- Date: 2026-10-18
- Requirements: KASI-RES-001, KASI-RES-002, KASI-RES-003, KASI-RES-006, TRD 6.5, 11.3, ADR 0003, ADR 0004

## Context

ADR 0003 fixed the checkpoint contract: a cursor may be recorded only after `FlushFileBuffers` returns. ADR 0004 fixed
when a split must be persisted. M1.7 proved a caller-supplied segment list can resume correctly, but a running transfer did
not write those cursors or the probe metadata to SQLite. A restart therefore had no durable place to resume from. M1.9 wires
the scheduler, part file, checkpoint writer and repositories together without giving the engine a dependency on `Kasi.Data`.

## Decision

**Checkpoint coordinator.** `SegmentCheckpointCoordinator` snapshots the scheduler with
`GetCheckpointSnapshot()` (segments and `StructureVersion` under one lock), flushes the part file, persists the structure
with `ISegmentRepository.ReplaceAsync` when the version changed, records every segment through `ICheckpointWriter` and
commits that batch. The snapshot is taken before the flush and the records are written after it. If the flush fails, nothing
is recorded.

**Live cadence and the loss budget.** The coordinator runs on a `PeriodicTimer` owned by the runner (`CheckpointInterval`,
**1.5 s** by default) and commits on every tick. Data lost on a kill is the interval plus `WriteBufferMaxAge` (0.5 s: bytes
still in a worker's write buffer are not in the snapshot), so 1.5 s plus 0.5 s keeps KASI-NFR-007's two seconds. It stops
before the runner's final snapshot/flush so the shutdown/pause checkpoint cannot race the periodic one. The final checkpoint
runs on success (before seal/commit) and on failure/cancellation, with the same snapshot-before-flush order.

**Structure.** `SegmentScheduler.GetCheckpointSnapshot()` returns the list and version atomically. A snapshot/version pair
cannot straddle a split, so a new segment is persisted before its progress is recorded. The initial structure is persisted
before any worker starts.

**Probe metadata.** After a successful probe the runner writes the final URL, resolved filename, total bytes, ETag,
Last-Modified and `Resumable` with `IJobRepository.UpdateTransferMetadataAsync`, which updates **only** those columns, so a
state change made by the caller in the meantime (a user pause) is never overwritten with a stale copy, and `done_bytes` stays
owned by the checkpoint writer. A job id that has no row is a `CheckpointError`, not a silent skip.

**Failure semantics.** A part file that cannot be flushed on a tick (disk full, I/O error) stops the transfer at once with its
disk classification (`DiskNotEnoughSpace` pauses, others fail), keeping the part file. A database failure is tolerated until
it has failed on `3` ticks in a row (a good tick starts the count again, so SQLite being briefly busy because of an indexer
or antivirus does not end a download); then the periodic checkpoint cancels the transfer, which returns
`TransferErrorKind.CheckpointError` with the last durable snapshot and the part file, instead of downloading on without a
durable record. A tick that failed earlier never fails a transfer that went on to complete: the final checkpoint before the
seal decides. `Cancelled`, `DiskNotEnoughSpace` and `CheckpointError` all return the last durable snapshot.

**Ownership.** The caller starts `CheckpointWriter` (its existing contract), creates the job row and supplies the repository
implementations; the runner uses only the Core interfaces. Job-state transitions and the engine facade remain M1.13.

## Consequences

- A pause/reboot can reload segment rows, send `If-Range` and continue without re-downloading flushed data.
- A kill loses at most `CheckpointInterval + WriteBufferMaxAge` (1.5 s + 0.5 s) per connection, as KASI-NFR-007 requires.
- A `/mutable` change between or during sessions returns `FileChanged`; the caller restarts, and a fresh download overwrites
  the part and replaces the old structure.
- Single-stream downloads have no resumable cursors and are restarted by the caller.
- Kill-loop, power-cut and reboot-under-load proof stays in M1.14; M1.9 proves the ordering and the integration path.
