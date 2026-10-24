# FFmpeg LGPL build and source offer

Kasi ships an **LGPL-2.1-or-later** FFmpeg/ffprobe build, invoked as an external process. No GPL or non-free components are
enabled and no FFmpeg code is linked into a Kasi assembly (KASI-NFR-020, ADR 0030).

## Build configuration

The minimal build uses:

```
--disable-gpl --disable-nonfree --disable-autodetect --disable-everything
--enable-protocol=file
--enable-demuxer=mpegts,mov,matroska,aac,mp3
--enable-muxer=mp4,matroska,ipod,mp3
--enable-bsf=aac_adtstoasc
```

`--disable-everything` plus the explicit list keeps the binary small and limits the attack surface. The wrapper only ever
passes local file inputs and `-protocol_whitelist file`.

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
