# 0026. Neutral media stream model and manifest parsers

- Status: Accepted (M3.1/3a)
- Date: 2026-10-24
- Requirements: KASI-MED-001, KASI-MED-002, KASI-MED-005 groundwork
- Plan: `docs/plans/m3.md` sections 2.1, 2.2 and 3 (PR 3a)

## Context

The extension will hand Kasi a manifest URL and the user must see a quality list before anything is downloaded. HLS and DASH
describe the same idea with different vocabularies; the downloader, picker and protection check all need one model. The
parsers also sit in front of hostile input from the internet, so they must be bounded before M3.2 adds a network loader.

## Decision

**One neutral model.** `MediaPresentation` contains `MediaVariant` entries (a video `MediaTrack`, its best matching audio,
and an optional subtitle), plus `AudioOnly` and `Subtitles`. A `MediaTrack` carries role, language, codecs, MIME, bitrate,
dimensions, duration, live flag, init segment, ordered `MediaSegment` list, optional `MediaKey` and any DASH protection
schemes. `MediaSegment` carries URL, duration, optional byte range, optional key and discontinuity; it is the unit M3.3
fetches and marks complete. Variants are highest quality first; `SelectDefault(1080)` is the model-level default rule.

**HLS is parsed as text.** `HlsParser.ParseMaster` reads `EXT-X-STREAM-INF`, `EXT-X-MEDIA` audio/subtitle renditions and
`EXT-X-SESSION-KEY`; `HlsPresentationBuilder` combines the master with caller-supplied media playlists. `ParseMedia` reads
`EXTINF`, byte ranges, `EXT-X-MAP`, `EXT-X-KEY`, discontinuities and `ENDLIST`. I-frame-only renditions are ignored. The
builder pairs the default/auto-select audio in the variant's `AUDIO` group; it never fetches.

**DASH is parsed as XML.** `DashParser.Parse` forbids DTDs and external resolvers, loads with a 10 MB character cap, resolves
nested `BaseURL`s with a depth and cycle check, and turns `SegmentTemplate` (`$Number$`, `$Time$`, `SegmentTimeline`),
`SegmentList` and `SegmentBase` into concrete segment URLs. A `SegmentBase` without `sidx` parsing is one whole-resource
segment with its initialization. `ContentProtection` schemes are carried on the track for the M3.2 gate; they are not
interpreted here.

**Bounds.** The manifest cap is 10 MB; variants, renditions, periods, adaptation sets, timeline entries and segments all have
hard caps before any expansion. Negative timeline repeats are rejected. Every parser failure is a `MediaParseException` with a
stable `MediaParseError`; the public API never leaks XML or URI exceptions.

**No download, no DRM decision.** 3a is pure parsing. 3b owns `MediaSourceLoader` (bounded fetch with cookies/referrer),
`ProtectionCheck` and the `Playable`/`Protected` result; 3c owns segment download and AES-128 decryption. The model carries
the key/protection signals so those layers do not need to re-parse the manifest.

## Consequences

- The picker can render variants and estimated sizes from one model without knowing HLS/DASH.
- HLS quality durations require loading the media playlists; 3b does that within its bounded fetch budget.
- SegmentBase on-demand files are represented conservatively as one segment; a later PR can split by `sidx` without changing
  the model.
- Hostile manifests fail fast with typed errors, and the fuzz targets in `Kasi.Fuzz` exercise both parsers nightly.
