# 0030. FFmpeg packaging and process boundary

- Status: Accepted (M3.4/3d)
- Date: 2026-10-24
- Requirements: KASI-MED-004, KASI-NFR-020, KASI-NFR-019
- Plan: `docs/plans/m3.md` sections 2.8, 2.9 and 7.2 (PR 3d)
- Related: ADR 0029

## Context

FFmpeg is the only external media dependency. It must mux the joined streams (and later probe their duration) without becoming
a linked library, without touching the network, without surviving a Kasi crash, and without pushing the installer past the
60 MB budget. The licence must remain LGPL.

## Decision

**External process only.** `MediaMuxer` invokes `FfmpegRunner`, which starts the configured `ffmpeg.exe`/`ffprobe.exe` with
`ProcessStartInfo.ArgumentList` (no shell), a bounded timeout, captured stdout/stderr and a Win32 job object with
`KILL_ON_JOB_CLOSE`. Two arguments are always present for muxing: `-nostdin` and `-protocol_whitelist file`; inputs are
absolute local files. FFmpeg never receives a URL and never has a network protocol enabled. The job object is disposed with
the process; if Kasi exits, Windows kills the child and its descendants.

**Mux policy.** `MediaMuxer` stream-copies, never transcodes. Video + audio go to `.mp4` when the manifest codec strings fit MP4
(H.264/HEVC/AV1 + AAC/AC-3/E-AC-3); otherwise `.mkv`. Audio-only keeps `.m4a` for AAC and `.mka` otherwise. The output is
written to a `.part.<ext>` sibling first and renamed after FFmpeg exits 0. `ffprobe` then checks the duration against the
manifest within one second; a mismatch fails the job in `Verifying` and keeps the joined/segment work folder. On success the
work folder is deleted.

**Redaction.** FFmpeg's stderr is passed through `FfmpegRunner.Sanitize`, which replaces known input/output/directory paths and
any absolute Windows path with `Redact.Path` (last segment or placeholder). The job log receives only the sanitized text;
the output path, query strings and cookies never reach it.

**Licence and packaging.** Only an LGPL build (`--disable-gpl --disable-nonfree`) is shipped. The minimal build enables the
file protocol, the demuxers we feed, the stream-copy muxers and the bitstream filters copy needs; it is a few MB, inside
`KASI-NFR-020`. The binary is a release asset with a pinned SHA-256, not checked into git and not a NuGet package. Kasi's
`THIRD-PARTY-NOTICES.txt` names FFmpeg, its LGPL version and the source offer; `docs/release-checklist.md` blocks a beta
without the licence text, source offer, notices entry and replaceable-binary check.

**Tests.** `tests/fixtures/media/muxable` contains checked-in H.264/AAC TS and fMP4 clips generated once by
`generate.ps1`; tests never run FFmpeg to create media. The mux tests locate a local FFmpeg via `KASI_FFMPEG`/`KASI_FFPROBE`
or `PATH`; when it is absent locally they do not exercise FFmpeg, and CI sets the variables (and therefore fails rather than
skipping silently). The wrapper itself runs with the same boundary in tests.

## Consequences

- The engine/Media assemblies contain no FFmpeg code or native library; only the path is configured.
- A killed Kasi cannot leave an FFmpeg child writing into the final file, and the `.part` output is safe to discard.
- A codec outside the MP4 list falls back to MKV instead of failing the capture.
- The minimal LGPL build is a packaging task with a pinned checksum; swapping it is a path/checksum change.
