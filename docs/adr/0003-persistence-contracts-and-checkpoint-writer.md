# 0003. Persistence contracts and the checkpoint writer

- Status: accepted
- Date: 2026-10-17
- Requirements: KASI-RES-001, KASI-RES-002, KASI-RES-006, KASI-NFR-009, TRD 6.5, TRD 11.2, TRD 11.3

## Context

M1.2 turns the TRD 11 schema into code the engine can use. The engine may not reference `Kasi.Data` (TRD 5.2), resume
correctness depends on never recording bytes as written before they are on disk (TRD 6.5), and several details are left open:
how progress is counted, what a failed commit does, how dates are stored and what the default categories' folders are.

## Decision

**Contracts and mapping.** `IJobRepository`, `ISegmentRepository`, `IQueueRepository`, `ICategoryRepository` and `ICheckpointWriter`
live in `Kasi.Core`; `Kasi.Data` implements them with Dapper. SQL aliases columns explicitly and private row classes convert to
the domain types; no global Dapper type handlers are registered. Dates are stored as UTC ISO-8601 text with 100 ns precision
(`2026-10-17T12:30:15.0000000Z`), which also sorts correctly. A stored state, kind, id, quota reset or segment range the model does
not accept raises `InvalidDataException` instead of being guessed or repaired.

**Progress.** Resume trusts `segment.written`, never `job.done_bytes`. `done_bytes` is a derived, informational total: the
checkpoint writer and `ISegmentRepository.ReplaceAsync` set it to the sum of the segments' written bytes, and `UpdateAsync` never
lowers it, so a stale in-memory job cannot undo a checkpoint. A restart goes through `ReplaceAsync` (new segments, nothing written,
atomic) or, for jobs without segments, `ResetProgressAsync`.

**Checkpoint writer.** A worker calls `Record(job, segment, written)` only after `FlushFileBuffers` on the part file has returned.
One background reader collects the updates from an unbounded channel (about one update per segment every 2 s) and commits all of
them in a single transaction every interval, on `FlushAsync` (pause, shutdown) and when stopped. Within a batch the highest value
per segment wins; a value never lowers what is stored and is clamped to the segment's range, so a bug in a producer cannot make a
segment unloadable. Updates for a job or segment that no longer exists change no rows and are ignored. A failed commit keeps the
updates, increments `FaultCount`, exposes `LastError` and is retried on the next tick; a failed `FlushAsync` reports the error to
the caller. What to do after repeated failures (pause jobs, warn the user) is the engine's decision (M1.9).

**Defaults (migration 0002).** The `setting` table, queue 1 "Main" (4 parallel, global defaults) and the seven default categories
plus a General fallback, all with fixed ids. Category folders are stored relative to the user's Downloads folder (`Video`, empty for
General); the application resolves them at run time because SQL cannot know the profile path (KASI-CAT-001, M2).

## Consequences

- Power-loss and kill-during-commit behaviour (TC-RES-003, TC-RES-011) is proven by the fault-injection harness in M1.14, not here.
- The engine must flush files before recording; nothing in the writer can check that, so M1.6 and M1.9 carry the obligation.
- Changing a stored text format, the date format or the checkpoint semantics needs a migration or a new ADR.
- Tables for media, schedules, category rules and torrent state are added by the milestones that need them.
