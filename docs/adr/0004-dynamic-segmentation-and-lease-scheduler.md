# 0004. Dynamic segmentation and the lease scheduler

- Status: accepted
- Date: 2026-10-17
- Requirements: KASI-ENG-004, KASI-ENG-005, KASI-ENG-006, KASI-RES-001, TRD 6.1 to 6.3, ADR 0003

## Context

TRD 6.2 and 6.3 describe segments, leases, splits, slow-lane takeover, endgame duplication and a connection ramp-up, but leave
gaps that decide whether the design works: who owns a segment between two leases, how the probe response becomes worker 0's
first lease, what progress may be checkpointed, and how a split reaches the database. This ADR fixes them. The scheduler itself
does no I/O: it is a state machine over `Segment` values that the transfer workers (M1.7) and the disk writer (M1.6) drive.

## Decision

**Model.** A segment is a byte range `[Start, End)` with a contiguous `Written` prefix. A worker fetches it in bounded leases
`[Cursor, min(Cursor + size, End))`, one request per lease. Lease size is two seconds of the worker's smoothed speed, clamped to
512 KiB to 16 MiB; a worker with no speed sample yet gets 512 KiB. Speed is an exponential moving average (alpha 0.3) of
throughput sampled once a second, the same figure the UI shows (TRD 6.8).

**Ownership.** A segment belongs to one worker from its first lease until it completes or the lease is abandoned. Completing a
lease does not release the segment, so the worker continues it. This keeps per-worker speed meaningful and stops two workers
alternating on one segment between leases. Abandoning a lease (a failure) releases the segment; the next lease starts at the cursor.

**Where an idle worker gets work, in order:** its own segment; an unowned incomplete segment; a slow-lane takeover; a standard
split of the segment with the most unleased bytes; an endgame duplicate. A split never cuts into a lease in flight
(`mid >= LeaseEnd`), so no request is aborted and the connection returns to the pool (TC-ENG-011, TC-ENG-010).

**Slow lane.** A worker whose speed stays below 30% of the median of the active workers for 10 s, whose segment has more than
4 MiB left and an unleased tail of at least 1 MiB, is split at the end of its current lease: it finishes that lease and the idle
worker takes the whole remainder. At least three active workers are required for the median to mean anything.

**Endgame.** When no split is possible and every remaining segment has under 1 MiB left, an idle worker duplicates the slowest
unfinished segment; whichever lease finishes first wins and the other is told it is superseded. Duplicated bytes are capped at
2 MiB per download.

**Ramp-up.** Start with one connection. After the first 250 ms with positive throughput add a second; afterwards add one per
250 ms window while the connection added last raised aggregate throughput by at least 5% of what an average existing connection
moves (`R_k - R_(k-1) >= 0.05 * R_(k-1) / (k-1)`), stopping for good at the first window that fell short. Each window is
measured from the moment the newest connection starts transferring, so its start-up time is not held against it, and no
connection is added when there is nothing left for it to fetch.

*Why not a flat 5% of the total, as TRD 6.3 reads.* On a link that scales perfectly the gain from connection k to k+1 is 1/k, which
drops below 5% at k = 20, so a flat threshold stops at 20 or 21 connections and TC-ENG-006 (count 32 reached) could never pass.
The marginal rule keeps ramping while each new connection still contributes like an average one, and stops at once on a
saturated link. The simulation tests show both, and show the flat rule failing the 32-connection case. `InitialConnections` and
the threshold are options so M1.15's benchmark can tune them.

**First lease.** For any download the engine intends to segment it probes with a bounded first range
(`firstRangeLength = 512 KiB`). The probe response is exactly worker 0's first lease and ends by itself, returning the
connection to the pool. An open-ended `Range: bytes=0-` stream is never extended into more leases, because one HTTP response
cannot be re-bounded and aborting it would close the connection. Open-ended probes remain the default of `ProbeAsync` and are
used when the engine does not segment.

**What may be checkpointed.** The scheduler counts bytes *received*. The disk layer takes `GetSegmentsSnapshot()` before
calling `FlushFileBuffers` and records those snapshot values with `ICheckpointWriter.Record` after the flush returns, so a
recorded cursor is never ahead of the data on disk (ADR 0003). The scheduler never reports flushed progress itself.

**Splits and the database.** `CheckpointWriter` only updates segment rows that exist, and ignores the rest. A split adds a row and
shortens another, so the scheduler raises `StructureVersion` on every split and the engine persists the snapshot with
`ISegmentRepository.ReplaceAsync` (at most 31 times per download) before recording progress for the new segment. A crash in
between costs at most that segment's progress, never correctness.

**Not scheduled.** Downloads of unknown size (chunked) and servers without range support use no scheduler: one stream, one
file. A known size without ranges is one segment and one worker.

## Consequences

- The scheduler is deterministic under a fake clock, so TC-ENG-006 to TC-ENG-011 and the 0 B to 64 GiB property test run in
  simulation; the live-server versions arrive with the real workers in M1.7.
- A large lease in flight near the end of the file can leave other workers idle, because a split cannot cut into it and endgame
  needs under 1 MiB left. An option shrinks tail leases to `remaining / connections`; it is off by default until M1.15 measures it.
- Ramp-up decided on 250 ms windows is noisy; a premature plateau costs throughput, not correctness.
- The probe's first range is part of the engine's protocol with `HttpProber`; changing it needs a new ADR.
