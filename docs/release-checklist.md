# Release checklist

Use this before publishing an installer or a beta. It complements `docs/plans/m3.md` section 7.2.

## FFmpeg (KASI-NFR-020, KASI-MED-004)

- [ ] The `FFmpeg minimal LGPL build` workflow is green on the commit being released and its artifact is the asset used below (the recipe in `docs/legal/ffmpeg-lgpl.md` is only a plan until then).
- [ ] Ship an LGPL build only: `--disable-gpl --disable-nonfree`; no GPL/AGPL/non-free codec.
- [ ] Record the FFmpeg version, release asset name and archive SHA-256 in the release notes.
- [ ] Run `build/Fetch-FFmpeg.ps1` against the pinned asset and confirm the checksum passes.
- [ ] Include the LGPL licence text and `docs/legal/ffmpeg-lgpl.md` (source offer) in the installer.
- [ ] Include the FFmpeg entry from `THIRD-PARTY-NOTICES.txt`.
- [ ] Confirm the installer size stays inside the 60 MB budget.
- [ ] Confirm Kasi finds FFmpeg by path and still starts when it is missing (media jobs fail with a clear error, other jobs run).
- [ ] Confirm no FFmpeg code is linked into a Kasi assembly and no FFmpeg network protocol is enabled.

## Media smoke

- [ ] `/hls/muxable` and `/dash/muxable` mux to MP4; `/hls/muxable` forced to MKV decodes in ffprobe.
- [ ] `ffprobe` duration is within one second of the manifest; a mismatch leaves the work folder.
- [ ] `/hls/muxable-aes128` produces the same playable output as the clear stream.
- [ ] FFmpeg stderr in the job log contains no full local paths, URLs, cookies or key URIs.

## General

- [ ] `dotnet test` green (Testcontainers on Docker where applicable).
- [ ] `dotnet format --verify-no-changes` green.
- [ ] Store packages, privacy policy and screenshots current.
