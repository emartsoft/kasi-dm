# 0007. Error classification, backoff and the pause hand-off

- Status: Accepted for M1.8
- Date: 2026-10-18
- Requirements: KASI-RES-004, KASI-RES-002, KASI-ENG-008, TRD 6.6, ADR 0003, ADR 0006

## Context

The M1.7 transfer runner stopped the whole transfer on the first failed lease so that resume and failure ordering were easy to
prove. The TRD requires a different behaviour: transient failures retry with full-jitter backoff, throttling honours
`Retry-After` and lowers the host connection count, permanent failures do not retry, and disk-full pauses preserve the part
file. The engine also has no job orchestrator yet, so global offline pause and the `Paused` job-state transition need a clean
hand-off rather than a hidden global singleton.

## Decision

**One policy, two seams.** `TransferRetryPolicy` classifies a `TransferFailed`/`TransferErrorKind` and returns
`TransferRetryDecision { Retry, Delay, ReduceConnections }`. The runner applies it at the probe boundary (including the
open-ended re-probe for an unknown total) and at each lease boundary.

**Classification.** Timeouts, resets, truncation/`ProtocolError`, `500`/`502`/`504` and `503` without `Retry-After` are
transient. `429`, and `503` with `Retry-After`, are throttled. `401`/`403` are auth (M1.10 refreshes them), `404`/`410` are
permanent, `FileChanged` is restart-only (M1.9), certificate failures are never retried, `DiskNotEnoughSpace` is a pause, and
`Cancelled`/offline stop the retry loop.

**Backoff.** Attempts are counted per probe and per worker; a successful lease resets the worker's counter. The cap is
`min(60 s, 1 s * 2^(attempt-1))`; the wait is full jitter in `[0, cap]`. A server `Retry-After` raises the wait to at least
the stated value. Ten failed attempts end the transfer with the original classified outcome.

**Throttling.** The first throttled decision in a transfer lowers the scheduler's allowed connection count by one; later 429s
from sibling workers do not reduce it repeatedly. The reduced `SegmentSchedulerOptions` also applies when the throttle
happens during the probe, before the scheduler exists.

**Lease retry.** A failed lease has already been abandoned, so its flushed cursor remains in the scheduler. The worker waits
and acquires a fresh lease; it never reuses a dead lease ID and never rewinds a cursor. Non-retryable failures still cancel
siblings and take the M1.7 snapshot/flush path.

**Pause hand-off.** `DiskNotEnoughSpace` and `Offline` are returned as `TransferFailed` with the durable snapshot and
classification; the caller pauses the job. Global "pause all jobs, resume on reconnect" belongs to the engine facade
(M1.13), which can feed an `INetworkStatus`/NLM signal into the same classification without the runner owning global state.
Single-stream downloads are restarted by the caller rather than resumed; they have no segment cursors to salvage.

## Consequences

- A transient lease failure no longer loses the transfer; the part file and scheduler stay alive.
- A permanent 404 fails after the first lease failure, not after ten backoffs.
- A throttled host eventually runs with fewer connections and the next job can start from the reduced setting.
- The retry waits are observable through `TimeProvider` in tests; production jitter is intentionally non-deterministic.
- Job-level pause/resume and offline auto-resume remain M1.13; M1.8 owns the classification and the runner half.
