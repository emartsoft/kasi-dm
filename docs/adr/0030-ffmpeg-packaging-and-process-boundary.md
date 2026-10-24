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
`ProcessStartInfo.ArgumentList` (no shell), a 30-minute default timeout, stdout/stderr read with a size cap (4 MiB and 64 KiB, the rest drained) and a Win32 job object
with `KILL_ON_JOB_CLOSE`. `-nostdin` is always present; every input is preceded by `-protocol_whitelist file` and, when the
stream type is known (init segment or `.ts`/`.m4s`/`.aac`/`.mp3` segments), by a forced `-f` demuxer, so FFmpeg never sniffs
downloaded bytes into a playlist or script format. Inputs are absolute local files. FFmpeg never receives a URL and never has a network protocol enabled. The job object is disposed with
the process; if Kasi exits, Windows kills the child and its descendants.

**Mux policy.** `MediaMuxer` stream-copies, never transcodes. Video + audio go to `.mp4` when the manifest codec strings fit MP4
(H.264/HEVC/AV1 + AAC/AC-3/E-AC-3); otherwise `.mkv`. Audio-only keeps `.m4a` for AAC and `.mka` otherwise. The output is
written to a `.part.<ext>` sibling, `ffprobe` checks its duration against the manifest within one second, and only a good file
is renamed to the real name: a failed mux, probe or mismatch deletes the `.part` file, leaves nothing at the output path and
keeps the joined/segment work folder. A file already at the output path is never replaced (`name (1).mp4`), and a dotted title
keeps its dots (the extension is only swapped when it is already a container extension). When the manifest has no `CODECS`,
the container is chosen from codec names `ffprobe` reads from the joined file. On success the work folder is deleted.

**Redaction.** FFmpeg's stderr is passed through `FfmpegRunner.Sanitize`, which replaces known input/output/directory paths and
any absolute Windows path with `Redact.Path` (last segment or placeholder). The job log receives only the sanitized text;
the output path, query strings and cookies never reach it.

**Licence and packaging.** Only an LGPL build (`--disable-gpl --disable-nonfree`) is shipped. The minimal build enables the
file protocol, the demuxers we feed, the stream-copy muxers and the bitstream filters copy needs; it is a few MB, inside
`KASI-NFR-020`. The binary is a release asset with a pinned SHA-256, not checked into git and not a NuGet package. Kasi's
`THIRD-PARTY-NOTICES.txt` names FFmpeg, its LGPL version and the source offer; `docs/release-checklist.md` blocks a beta
without the licence text, source offer, notices entry and replaceable-binary check.

**Tests.** `tests/fixtures/media/muxable` contains checked-in H.264/AAC TS and fMP4 clips generated once by
`generate.ps1`; tests never run FFmpeg to create media. The mux tests locate FFmpeg via `KASI_FFMPEG`/`KASI_FFPROBE` or `PATH`; when it is absent locally they do not exercise FFmpeg.
`ci.yml` fetches a checksum-pinned third-party LGPL build (BtbN autobuild, FFmpeg 8.1.3 shared; digest from the release API),
uses it for tests only and never ships it, and sets the variables, so CI fails rather than skipping silently. Kasi's own
minimal build is validated by the manual `ffmpeg-minimal-build.yml` workflow, which builds the recipe from a pinned source
tarball, proves it LGPL and runs these tests against it; it must be green before a beta. The wrapper itself runs with the same boundary in tests.

## Consequences

- The engine/Media assemblies contain no FFmpeg code or native library; only the path is configured.
- A killed Kasi cannot leave an FFmpeg child writing into the final file, and the `.part` output is safe to discard.
- A codec outside the MP4 list falls back to MKV instead of failing the capture.
- The minimal LGPL build is a packaging task with a pinned checksum; swapping it is a path/checksum change.
