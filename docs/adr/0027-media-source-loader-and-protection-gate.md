# 0027. Media source loader and the single DRM gate

- Status: Accepted (M3.2/3b)
- Date: 2026-10-24
- Requirements: KASI-MED-005, KASI-MED-006 groundwork, KASI-NFR-013
- Plan: `docs/plans/m3.md` sections 2.2 and 3 (PR 3b)
- Related: ADR 0026

## Context

M3.1 parsers are pure: they can be called with any text and carry key/protection signals. M3.2 must fetch the manifest with the
page's cookies and referrer, run the protection decision **before any segment request**, and make it impossible for another
code path to reach a segment with a plan that skipped the check.

## Decision

**One entry point.** `MediaSourceLoader.LoadAsync(kind, manifestUri, context)` is the only public way to obtain a playable
plan. It fetches the manifest (and HLS media playlists), parses, runs the gate and returns a nested `Result`:
`Playable` or `ProtectedResult`. `Playable` has no public constructor and only an internal factory; no outside assembly can
create one. The segment downloader in M3.3 accepts only `Playable`.

**Bounded fetch.** The loader uses an injected or default `HttpClient` with no automatic redirects, manual redirects capped at
10, and `ReadAsStream` with a 10 MB cap. HLS media playlist fetches are capped at 256. Cookies are matched by host, path and
Secure flag against every hop; the referrer is sent only to the initial manifest origin; the origin/user-agent are optional.
Error messages never echo a manifest URL or its query; the refusal carries only a reason code and the fixed message "This video
is protected and can't be saved".

**HLS gate.** For every master session key and every media-playlist/segment key: `NONE` is clear; only `AES-128` with
`KEYFORMAT` absent/`identity` and an absolute **HTTPS** key URI is allowed. SAMPLE-AES, SAMPLE-AES-CTR, any other key format and
unknown methods are refused. The M3.1 builder propagates `EXT-X-SESSION-KEY` signals to every track; the loader also checks
the raw keys, so a signal cannot be lost while building variants.

**DASH gate.** Any used `ContentProtection` scheme on a track — including `urn:mpeg:dash:mp4protection:2011` and unrecognised
URIs — refuses the presentation. No key is decrypted here; AES-128 HLS is the only encryption M3 supports and the key is never
written to disk.

**Fail closed, no override.** There is no setting, flag, parameter or environment variable that can skip the gate. The
architecture tests assert `Playable` has no public constructor and no public media member suggests `bypass`, `skip`, `ignore`
or `drm`. Two approvals and the `security` label are required on the PR.

**Fetch before the gate is limited to manifests.** HLS media playlists are manifests, not segments; tests use a per-path server
counter to prove zero `.ts`, `.m4s`, init and key-path requests for every refused kind.

**One load has a memory budget.** Besides the 10 MB cap per manifest, a load may hold 32 MB of manifest text and 250,000 segments in total across the master and all media playlists, so many near-cap playlists cannot add up to unbounded memory (TPL 7.2).

**The page referrer follows the hop's origin.** It is sent to the manifest origin, which CDNs check, and dropped on a redirect to another origin; cookies are matched per hop.

## Consequences

- M3.3 can accept only a `Playable`; the type system enforces the gate.
- Cookie/referrer behavior is local and testable without a browser.
- HLS AES-128 over plain HTTP is refused by design; the TLS test server exercises the allowed path.
- A refused capture cannot leak segment URLs or key URIs into logs or the toast.
