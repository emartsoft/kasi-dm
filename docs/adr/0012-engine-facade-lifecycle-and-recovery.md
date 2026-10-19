# 0012. Engine facade lifecycle, recovery and pause reasons

- Status: Accepted incrementally for M1.13 (13a landed first; 13b/13c extend it)
- Date: 2026-10-19
- Requirements: KASI-ENG-008, KASI-RES-001, KASI-RES-002, KASI-RES-003, KASI-RES-006, KASI-NFR-007, KASI-NFR-009, KASI-NFR-019
- Related: ADR 0002, ADR 0003, ADR 0007, ADR 0008

## Context

The M1.7–M1.12 runners deliberately stop at a classified `TransferOutcome`. The job row, its state and the decision to resume,
pause or restart were left to a caller. M1.13 introduces `DownloadEngine`, the headless facade that owns that caller role. It
must persist a transition before notifying anyone, recover interrupted jobs at start-up, and keep explicit why a job is paused.

## Decision

**13a — the core.**

- The database is the truth. A state change is a compare-and-set (`UPDATE … WHERE id = @id AND state = @from`) in
  `IJobRepository.TryChangeStateAsync`, which also writes the error, the pause reason and the completion timestamp. It never
  touches `done_bytes`; the checkpoint writer owns progress.
- `JobStateMachine.RecoveryNextStates` is a separate, explicit set of recovery-only edges. Normal `CanTransition` is unchanged.
- `PauseReason` (`UserRequested`, `NotEnoughSpace`, `NetworkOffline`, `CrashRecovery`, `CheckpointFault`) is stored in a new
  nullable `job.pause_reason` column (migration 0003). Only `NetworkOffline` may be resumed automatically.
- Events go through a bounded channel consumed by one dispatcher. `JobStateChanged` is published after the commit; a throwing
  subscriber is counted and never blocks the engine.
- `DownloadEngine.StartAsync` runs `PRAGMA quick_check`, then crash recovery before accepting work. Recovery covers final-file
  completion, a valid part/segment set (reconcile and pause with `CrashRecovery`), a reset for an unusable part, and Probing to
  Queued. `StopAsync` cancels running jobs so their final checkpoint runs.
- `AddJobAsync` serialises `DuplicateDetector` and the insert, returning a conflict instead of adding a duplicate. The caller
  chooses Overwrite/AutoRename/Resume.
- Only HTTP and FTP are dispatched; Media and Torrent are rejected with a clear error.
- The facade passes the checkpoint writer, segment repository, job repository, secret repository, bandwidth limiter and proxy
  settings through `HttpTransferOptions`.

## Consequences

- A subscriber that queries the row from `JobStateChanged` sees the new state, because the event is raised after the commit.
- Recovery never rewinds `done_bytes`; a valid part is reconciled from segment cursors and paused, not silently restarted.
- A database that fails `quick_check` stops the engine before any job is touched.
- 13b adds pause/resume/cancel/offline/link refresh; 13c adds changed-file restart, the kill-loop smoke test and the headless
  end-to-end proof. The normal state machine remains closed until those explicit paths need a new edge.
