# FFmpeg LGPL build and source offer

Kasi ships an **LGPL-2.1-or-later** FFmpeg/ffprobe build, invoked as an external process. No GPL or non-free components are
enabled and no FFmpeg code is linked into a Kasi assembly (KASI-NFR-020, ADR 0030).

## Build configuration

The recipe below is Kasi's own minimal build. The manual workflow `.github/workflows/ffmpeg-minimal-build.yml` compiles it from
the pinned FFmpeg 8.1.3 source tarball (SHA-256 in the workflow), checks that the result is LGPL and runs the media tests
against it. It was verified on 2026-10-24: run 40114108663, `ffmpeg.exe` 3.7 MB and `ffprobe.exe` 3.5 MB, all 56 media tests
passing. Stream copy of ADTS AAC from MPEG-TS needs the native audio decoders (the first run failed with "Could not find codec
parameters ... unspecified sample rate" without them); a video codec that fails the same way gets its native decoder added here.
The workflow artifact (binaries, source tarball, configuration, SHA-256) is what a release uses; the release checklist requires
the workflow to be green on the release commit. CI for pull requests keeps using the pinned stock LGPL build (ADR 0030).

Options:

```
--disable-gpl --disable-nonfree --disable-autodetect --disable-everything
--enable-protocol=file
--enable-demuxer=mpegts,mov,matroska,aac,mp3,ac3,eac3
--enable-muxer=mp4,matroska,ipod,mp3
--enable-parser=h264,hevc,av1,aac,aac_latm,ac3,mpegaudio,vp9,opus
--enable-decoder=aac,aac_latm,ac3,eac3,mp3float
--enable-bsf=aac_adtstoasc,extract_extradata
```

`--disable-everything` plus the explicit list keeps the binary small and limits the attack surface. The wrapper only ever
passes local file inputs, `-protocol_whitelist file` before every input and, when the stream type is known, a forced
input demuxer (`-f mpegts` or `-f mp4`) so it never sniffs downloaded bytes into a playlist or script format.

## Release asset

The binary is **not** checked into git and is **not** a NuGet package. It is a release asset:

- file name, FFmpeg version and the SHA-256 of the archive are recorded in the release notes;
- `build/Fetch-FFmpeg.ps1` downloads the asset and refuses a checksum mismatch;
- the installed files live beside the app and are located by path; replacing them does not require rebuilding Kasi.

## Source offer

For every binary release, Kasi offers the corresponding FFmpeg source and the configuration/scripts used to build it. The
offer is included with the installer and in the release notes; a written offer is kept for at least three years. Contact:
the support address in `README.md`.

## Compliance gate

The release checklist requires the licence text, this source offer, the `THIRD-PARTY-NOTICES.txt` entry, the pinned
checksum and the replaceable-binary check before a beta or stable build is published.
