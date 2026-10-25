# ADR 0035 — Quota accounting and wake timers (M3.8)

- **Status:** Accepted
- **Date:** 2026-10-25
- **Requirement:** KASI-QUE-005, KASI-QUE-007; TC-QUE-005, TC-QUE-006
- **Context:** The queue already has `quota_bytes`/`quota_reset` columns, but no usage table, no byte seam from the
  engine, and no way to wake a sleeping PC for a scheduled start. Both features must work after an MV3-style restart
  of the app and must stay testable without hardware.

## Decision

### Data quota

- `queue_usage(queue_id, period_start, bytes)` stores consumption per queue and period. `period_start` is a UTC
  instant; local midnight (daily), local Monday 00:00 (weekly), and the Unix epoch (never) are computed by
  `QuotaPeriod` from `TimeProvider`, so a time-zone change does not rewrite history.
- The engine gets a Core-level `IJobByteUsageSink`. It reports **deltas** of bytes written, not absolute progress, so a
  resumed job does not count its saved bytes twice. The same seam can be implemented for media later without changing
  Scheduling.
- `QueueQuotaService` reserves the known remaining size (`TotalBytes - DoneBytes`) before a job starts. A queue stops
  issuing work when `used + reserved >= cap`. Unknown-length transfers reserve zero, so the soft cap can be exceeded
  by at most the existing write-chunk bound per connection. This is the accepted overshoot in plan section 7.5.
- `QueueRunner` stops the queue when a reservation fails or `QuotaReached` arrives, pauses active jobs, and records
  `PauseReason.Quota`. `pause_reason` is text, so no schema migration is needed for the new value.
- At the next tick after the period rolls over, `QueueScheduler` asks the runner to requeue quota-paused jobs and
  start the queue again, in queue order. The check also sees quota pauses persisted across an app restart.
- The Scheduler window edits the cap in GB and the reset period, and explains the soft-cap behaviour. Quota values are
  stored as bytes; the UI conversion is exact for the entered decimal.

### Wake from sleep

- `IWakeTimer` is the Core seam. `WindowsWakeTimer` uses `CreateWaitableTimerExW`/`SetWaitableTimer` with the resume
  flag and waits on the handle on a worker thread. A later `Arm` replaces the earlier instant; `Disarm` cancels it.
- `QueueScheduler` computes the next enabled `StartTime` through `ScheduleEvaluator.NextStart` and arms the timer on
  every evaluation (including the 30-second tick, so a schedule edit is picked up without an event). The timer also
  re-evaluates when it fires, so the next start is armed again.
- `IWakeTimerPolicy` reads the active power plan's RTC-wake setting through `PowerReadACValueIndex`/
  `PowerReadDCValueIndex`. When it is disabled the Scheduler shows a warning naming “Allow wake timers”; Kasi does not
  silently arm a timer that Windows will ignore.
- Unit tests use fakes and `FakeTimeProvider`. The hardware case is manual: `docs/qa/wake-from-sleep.md` records the
  three owner laptops, their `powercfg /a` result and three sleep/hibernate/lid runs. KASI-QUE-005 stays **code
  complete, not verified** until that evidence exists; the untested sleep type is kept on the release checklist.

## Consequences

- Quota accounting is one small table and one synchronous sink; it has no dependency on the media types.
- The soft cap is honest: the UI says “may exceed by a few MB”, and tests assert the reservation bound rather than an
  impossible exact stop for unknown-length responses.
- Wake timers can be disabled by policy without breaking schedules; Kasi warns instead of pretending it can wake.
- The hardware gap is explicit and cannot be mistaken for a green TC-QUE-005.
