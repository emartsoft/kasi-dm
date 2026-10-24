# 0031. Media capture in the app: events, recovery and the capture flow

- Status: Accepted (M3.5 glue, PR 3e)
- Date: 2026-10-24
- Requirements: KASI-MED-001, KASI-MED-005, KASI-QUE-001, KASI-RES-002
- Plan: `docs/plans/m3.md` decisions 3, 6, 12 to 14 (PR 3e)
- Related: ADR 0027, ADR 0029, ADR 0030

## Context

A captured stream is one job that runs outside the transfer engine (`MediaJobExecutor`, `MediaJobRunner`). The grid, tray,
notifications and the queue runner learn about jobs only through the engine's event dispatcher, and the engine recovers jobs
after a crash by their part files. A media job therefore needs to join that event stream and to be recovered by its own
executor.

## Decision

**One event stream.** `DownloadEngine.PublishExternal` puts an event on the engine's dispatcher. The capture service publishes
`JobAdded`; the executor and the runner publish `JobStateChanged`, `JobProgressed` (done bytes, estimated total, average speed)
and `JobLogged` (FFmpeg stderr after path redaction, at most 20 lines). The queue runner's slot accounting, the grid and the
tray need nothing media-specific.

**Capture flow.** `captureMedia` (HLS or DASH) loads the manifest with the page's cookies, referrer, user agent and origin. A
protected stream is refused with the exact message and creates no job and no segment request. Otherwise one `Media` job is added
(category rules choose the folder as for any download, the same stream clicked twice is one job), its secret is saved, and the
job is started through the routing control, the same start-now semantics as an HTTP add. Direct media is an ordinary HTTP job.
The browser is answered within `AckBudget` (5 s); a slower capture finishes in the background. A failed capture is an `error`
with its reason in `detail`.

**Pause and resume.** Pause cancels the download and moves the job to Paused (user requested); a job already muxing or verifying
is left to finish. Start from Paused resumes: finished segments are not fetched again (ADR 0029).

**Recovery.** `DownloadEngine` recovers only HTTP and FTP jobs. `MediaJobExecutor.RecoverAsync`, run after the engine starts,
requeues a media job caught probing, downloading or verifying (progress is kept), completes one whose file exists, and runs a
job caught muxing again from its joined files.

**Failure and secrets.** Without FFmpeg a media job fails at once with a clear message instead of waiting in Muxing. A mux
failure fails the job with its reason and logs the sanitized FFmpeg lines. The job's cookies are deleted when it completes.

**Bounded catalogue.** The per-tab stream catalogue keeps the newest 64 streams per tab and the 256 most recently touched
tabs.

## Consequences

- Media jobs behave like other jobs in the grid, tray, notifications and queue runner.
- The proxy, a per-job speed limit and the segment parallelism setting are not yet passed to the media runner; the global
  bandwidth limiter is.
- A protected capture is acknowledged with the refusal text in `detail`; showing it to the user is the extension's job (3g).
