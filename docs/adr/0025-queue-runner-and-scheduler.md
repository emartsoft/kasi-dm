# ADR 0025: Queue runner and scheduler

- Status: Proposed (M2.10)
- Date: 2026-10-23
- Requirements: KASI-QUE-001, KASI-QUE-002, KASI-QUE-003, KASI-QUE-004
- Plan: `docs/plans/m2-10.md`

## Context

After M2.9 the app has a full main window with queue sidebar entries and disabled toolbar buttons ("Start queue", "Stop queue", "Scheduler"). The engine processes individual downloads when instructed, but queued jobs do not progress on their own.

TRD 10 requires:
1. Each job belongs to one queue, and queues run with a configurable parallel limit (1–20, default 4).
2. The queue runner picks the next job in queue order (`queue_pos`) whenever a slot is free, and moves repeatedly failing jobs to the end of the queue.
3. Jobs can be reordered within a queue and moved between queues, with order preserved across restarts (`queue_pos` fractional indexing).
4. Queues can be scheduled to run during specific daily or overnight time windows and days of the week, with missed schedule handling on startup.
5. Post-completion actions (Sleep, Hibernate, Shutdown, Exit Kasi, Run program) can run when a queue finishes, with a 60-second cancellable countdown for power actions and a multi-queue busy guard.
6. `Kasi.Scheduling` is constrained by architecture rules (`SolutionLayout.cs`) to reference only `Kasi.Core`.

## Decision

1. **Architecture isolation via `IJobControl`:**
   `Kasi.Core` defines `IJobControl`:
   ```csharp
   public interface IJobControl
   {
       Task StartJobAsync(JobId id, CancellationToken cancellationToken = default);
       Task PauseJobAsync(JobId id, CancellationToken cancellationToken = default);
   }
   ```
   `DownloadEngine` in `Kasi.Engine` already implements both methods and implements `IJobControl`. `QueueRunner` in `Kasi.Scheduling` coordinates job execution strictly through `IJobControl`, `IJobRepository`, and `IQueueRepository`.

2. **Fractional Indexing for Reordering (`queue_pos`):**
   `job.queue_pos` is stored as a `REAL` (floating-point) value. Moving a job between two adjacent jobs with positions $A$ and $B$ assigns $(A + B) / 2.0$. Moving to the head assigns $A / 2.0$ (or $A - 1.0$), and to the tail assigns $Z + 1.0$. Reordering or moving queues requires only a single SQL statement:
   `UPDATE job SET queue_id = @queueId, queue_pos = @queuePos WHERE id = @id;`
   If floating point precision between two adjacent items becomes $< 10^{-6}$, positions are rebalanced at uniform intervals ($1000.0, 2000.0, \dots$).

3. **Schedule Windows & Overnight Evaluation:**
   A schedule is defined by an enabled flag, a start time, an optional stop time, and a day-of-week bitmask. For overnight schedules spanning midnight (where `StartTime > StopTime`, e.g. 23:00 to 06:00), `ScheduleEvaluator` determines active status based on the preceding day's mask for early morning hours and current day for late evening hours. Time-based transitions and missed-schedule checks are driven by injected `TimeProvider`.

4. **Post-Completion Actions & System Execution:**
   When a queue's last active job completes and no queued jobs remain, `PostActionCoordinator` evaluates the configured action. Power actions verify whether other queues have active transfers (skipping power actions unless `ForceIfOtherQueuesBusy` is checked) and start a 60-second cancellable countdown. `IPowerController` abstracts OS power APIs (`SetSuspendState`, `InitiateShutdown`), ensuring unit and CI tests run safely via test doubles.

## Consequences

- Queue limits are enforced deterministically and jobs progress automatically as slots open up.
- Reordering is $O(1)$ database writes without table-wide renumbering.
- Time evaluation is 100% deterministic and testable using `FakeTimeProvider` without wall-clock sleep delays.
- System power actions are isolated behind interfaces, guarding against unintended host machine shutdowns during testing.

## Review amendments

- A queue reports completion only after it ran something since it was started, never while a job is queued, active or paused, and it stops being "running" when it reports it, so an idle queue cannot fire a post-action and a finished one is not counted as busy.
- The 60-second countdown checks again when it expires: if any queue is running (and the action is not forced), the power action is skipped.
- The scheduler starts a queue when its window opens (or at the first look), not on every tick inside the window, so a queue stopped by hand stays stopped.
- Shutdown no longer forces other applications closed. `%1` is replaced by the file path in quotes. Background failures of post-actions and scheduler ticks are raised as `ActionFailed` and `EvaluationFailed` instead of being swallowed.
