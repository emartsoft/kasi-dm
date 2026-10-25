# 0032. The media picker and the saved choice

- Status: Accepted (M3.6, PR 3f)
- Date: 2026-10-24
- Requirements: KASI-MED-002, KASI-UI-010
- Plan: `docs/plans/m3.md` decisions 12 and 13 (PR 3f)
- Related: ADR 0034

## Context

A captured stream offers several qualities and containers. The person chooses once, in a window, but the job runs later and can
be resumed after a restart. The job reloads the manifest each time it starts, and HLS playlist URLs are often signed and change
on every load, so the choice cannot be remembered as a URL.

## Decision

**One dialog at a time.** The capture service shows the picker through `IMediaPicker`; a second click on a stream waits behind
the open dialog and then finds the first one's job instead of opening another picker. Cancelling creates no job and is
acknowledged to the browser as handled.

**The choice is saved with the job, typed.** `JobSecret.Media` (`MediaSelection`: container, height, bitrate, codecs, language)
travels with the job's cookies and is deleted with them. It is not a request header. The default quality is the highest at or
below 1080p; the preferred-maximum setting arrives with the Settings page.

**Matched by height and nearest bitrate.** On start or resume the executor looks the choice up in the freshly loaded manifest by
height and the nearest bitrate (`MediaPresentation.FindVariant`, `FindAudioOnly`). A choice the stream no longer offers fails the
job with a clear message; it never changes quality silently.

**Audio only is one stream.** An audio-only variant has the same track in both slots; the runner fetches it once.

**Container rule is shared.** The picker's default container uses `MediaMuxer.IsMp4Compatible`, the same rule the muxer applies
for `Auto`.

**Posters are fetched defensively.** http and https only, no cookies, a 10 s timeout and a 5 MB cap; the image is decoded and
bound on the UI thread. A missing or oversized poster never blocks the picker.

## Consequences

- A rotated playlist URL no longer changes the quality a person chose.
- The picker holds the capture open until it is answered; the browser has already been acknowledged by then (ADR 0034).
- Subtitles are listed but disabled until 3j.
