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

**Live cadence.** The coordinator runs on a `PeriodicTimer` owned by the runner (`CheckpointInterval`, 2 s by default). It
stops before the runner's final snapshot/flush so the shutdown/pause checkpoint cannot race the periodic one. The final
checkpoint runs on success (before seal/commit) and on failure/cancellation, with the same snapshot-before-flush order.

**Structure.** `SegmentScheduler.GetCheckpointSnapshot()` returns the list and version atomically. A snapshot/version pair
cannot straddle a split, so a new segment is persisted before its progress is recorded. The initial structure is persisted
before any worker starts.

**Probe metadata.** After a successful probe the runner updates the job row with the final URL, resolved filename, total
bytes, ETag, Last-Modified and `Resumable`. `done_bytes` remains owned by the checkpoint writer and `SegmentRepository`; the
runner never lowers it.

**Failure semantics.** A part-file flush failure keeps the existing `DiskError`/`DiskNotEnoughSpace` classification and the
part file. A database/structure failure is `TransferErrorKind.CheckpointError`; the transfer stops rather than continuing
without a durable record. `Cancelled`, `DiskNotEnoughSpace` and `CheckpointError` all return the last durable snapshot.

**Ownership.** The caller starts `CheckpointWriter` (its existing contract), creates the job row and supplies the repository
implementations; the runner uses only the Core interfaces. Job-state transitions and the engine facade remain M1.13.

## Consequences

- A pause/reboot can reload segment rows, send `If-Range` and continue without re-downloading flushed data.
- The 2 s interval bounds replay to at most roughly 2 s per connection, as required by KASI-RES-001.
- A `/mutable` change between or during sessions returns `FileChanged`; the caller restarts, and a fresh download overwrites
  the part and replaces the old structure.
- Single-stream downloads have no resumable cursors and are restarted by the caller.
- Kill-loop, power-cut and reboot-under-load proof stays in M1.14; M1.9 proves the ordering and the integration path.
