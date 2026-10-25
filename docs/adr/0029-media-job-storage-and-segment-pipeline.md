# 0029. Media job storage, segment download and ordered assembly

- Status: Accepted (M3.3/3c)
- Date: 2026-10-24
- Requirements: KASI-MED-003, KASI-MED-006, KASI-RES-004, KASI-NFR-012
- Plan: `docs/plans/m3.md` sections 2.4, 2.5 and 2.7 (PR 3c)
- Related: ADR 0033, ADR 0027

## Context

A media capture is one job in the queue but many HTTP resources. It needs its own progress model, durable segment completion,
safe resume after a kill, AES-128 decryption and an ordered per-stream file that the M3.4 mux step can consume. The ordinary
`segment` table is for byte ranges of one HTTP file and cannot express segment URLs, per-stream initialization or completion.

## Decision

**Rows, not grid segments.** Migration `0007_media_streams.sql` adds `media_stream` (job, index, role, language, codecs,
container, variant id, estimated bytes, init URL/size) and `media_segment` (job, stream, index, URL, size, completed, IV,
discontinuity, range start/end). A media job keeps one `job` row; the grid shows the job, not thousands of segments. Deleting the
job cascades to both tables. `job.total_bytes` is the manifest estimate (bandwidth × duration, approximate);
`job.done_bytes` is the sum of completed segment sizes.

**One completion writer.** Each segment is fetched as a whole resource, decrypted if necessary, written to
`<root>/<job>/<stream>/<index>.part`, flushed with `FlushFileBuffers`, renamed to `<index>`, and only then queued for SQLite.
`MediaJobRunner` batches those completions every `CompletionInterval` (1 s in production, shorter in tests); one writer updates
`media_segment` and `job.done_bytes` in a single transaction. Initialization segments are written `.part` -> `init` the same
way. Resume deletes stray `.part` files, skips completed segments whose final file exists, re-fetches a completed row whose file
is missing, and re-joins when every segment is present.

**Concurrency and transport.** Up to `Parallelism` segments (default 6, allowed 1–16) are in flight per stream; video and audio
run side by side. Segments use `HttpHostPool` lanes, `TransferRetryPolicy`, the shared `BandwidthLimiter` and the page
cookies/referrer from `MediaRequestContext`; a byte-range segment sends `Range: bytes=start-end`. Network resets, 5xx/429
responses, truncated bodies and timeouts retry through the policy; a 4xx failure is not retried.

**AES-128.** Only the `Playable` plan from ADR 0027 reaches this runner. Keys are fetched once per key URI, held in memory
only, never written to SQLite or logs; the key URI must be HTTPS. CBC/PKCS#7 uses the explicit `IV` when present, otherwise the
segment's media sequence number (`EXT-X-MEDIA-SEQUENCE` + index, carried on `MediaSegment.Sequence` and stored as an explicit IV in `media_segment`) as 16 big-endian bytes (RFC 8216). Decryption happens before the segment file is renamed and
before it is marked complete.

**Init byte ranges, limits and failure.** `media_stream` keeps the init range (`init_range_start`/`init_range_end`) so a
`BYTERANGE` init segment fetches only its range; a DASH `SegmentBase` whose init is the media file itself is one whole segment
with no separate init. A segment or init body is held in memory up to 64 MB and a key up to 1 KiB. The first non-cancellation
failure cancels the run's other segment fetches and is the error reported. All per-run state (parallelism, worker counter,
cancellation) lives in the run, so one `MediaJobRunner` serves several jobs at once. A failed completion save fails the run as a
disk error, and a flush never waits out the batch interval. The page referrer is sent to a segment's own origin, not to a
cross-origin redirect target.

**Ordered assembly.** Per stream, `init` (if any) followed by segments in index order is copied into
`<job>/joined/<stream>.bin`. The job moves `Downloading -> Muxing`; M3.4 invokes FFmpeg on these files and owns the final
container, duration check and folder cleanup. Three-C does not mux or delete the segment folder.

**Parent job and errors.** `MediaJobRunner` uses `IJobRepository.TryChangeStateAsync`; an unrecoverable failure moves the job
to `Failed` with a message that contains no segment URL. Disk paths and errors are classified as disk/disk-full, and a failed
segment leaves neither a completed row nor a `.part` file.

## Consequences

- A kill at any point resumes from the last flushed/renamed segment; SQLite is never ahead of the files.
- The app's M3.5 protocol can expose segment-level progress without a grid row per segment.
- Signed segment URLs and init URLs are persisted in SQLite; like `job.url` they must not be logged.
- Subtitles and live streams remain out of scope (3j/3k); the tables already have a `subtitle` role and discontinuity flag.
- Two writers adding a migration must renumber: 3h takes the next free number after 0007.
