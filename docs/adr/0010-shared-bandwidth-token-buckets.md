# 0010. Shared bandwidth token buckets

- Status: accepted
- Date: 2026-10-19
- Requirements: KASI-BWL-001, KASI-BWL-002; TC-ENG-024

## Context

HTTP jobs need one combined download cap and an optional cap for each job. Later torrent traffic must draw from the same global budget. A network stall timer must measure time spent waiting for the server, not time deliberately spent waiting for tokens. The TRD specifies reads no larger than 16 KiB and a burst capacity of one quarter second of the configured rate.

## Decision

`BandwidthLimiter` owns a global token bucket and creates job bucket handles. Callers share one limiter across jobs; a future torrent adapter can call `AcquireGlobalAsync`. A worker reserves bytes from both buckets under one lock before every body read, including a superseded lease drain, so waiting for a job cap does not consume global tokens. The worker returns unused bytes after a short read, EOF or read failure. Both rates can change while workers run; a change wakes blocked workers. A null rate is unlimited, and a positive rate is in bytes per second.

Each bucket refills using `TimeProvider` timestamps and stores fractional tokens. Its capacity is `max(1 byte, rate / 4)`, and one reservation is at most 16 KiB. The smallest rates therefore make progress without exceeding the quarter-second burst in normal ranges. The HTTP runner pauses its read-stall timer during token waits and disk work, then arms it for the network read itself.

## Consequences

The owner of the engine facade must pass the same `BandwidthLimiter` to every HTTP job and the later torrent adapter. `HttpTransferOptions.JobBandwidthLimit` must come from that limiter. Bandwidth is shaped at the client read boundary; operating-system and HTTP buffers may receive data ahead of the read, so tests measure consumed client bytes over a ten-second window. Reserving 16 KiB can temporarily count bytes that are in flight; unused tokens are returned when the read finishes. The fast completed-acquisition path returns a `ValueTask<int>` without allocating a per-chunk buffer.
