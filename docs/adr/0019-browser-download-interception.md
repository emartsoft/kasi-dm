# 0019. Browser download interception

- Status: Proposed (rule semantics in M2.4a; the hand-off budget, bypass detection and the end-to-end harness are added by M2.4b and M2.4c)
- Date: 2026-10-21
- Requirements: KASI-BRW-002, KASI-BRW-003, KASI-NFR-010
- Related: TRD 9.2 and 9.4; `docs/plans/m2-4.md`; ADR 0016, ADR 0018

## Context

`rules` carried extension, MIME and site lists with no stated semantics, and no size field although BRW-002 names a minimum size. Kasi and the extension must decide alike, and the strict parsers on both sides reject any field the other side does not know.

## Decision

**Rule semantics** are written into TRD 9.2 and `docs/protocol/README.md` and implemented as pure functions in `extension/src/shared/rules-match.ts`: a download is taken when rules exist and are enabled, it is not excluded, and its extension or its MIME type matches. Extensions are lowercase without a dot and match the end of the file name; MIME types are exact or `type/*`; an empty list matches nothing (never take what was not asked for); an excluded site covers its subdomains and is tested against the URL, the final URL and the referrer. `blob:`, `data:`, other schemes, URLs over 8,192 characters, another extension's downloads and private windows (unless allowed) are never taken.

**Minimum size** is an optional `minSizeBytes` (integer, 0 to 2^53-1) on `rules`. Because a strict parser rejects the whole message, Kasi sends it only when `hello.extensionVersion` is 0.2.0 or later (`IpcProtocol.MinSizeExtensionVersion`), and `v` stays 1. The extension is released as 0.2.0 with the field. A download of unknown size is intercepted; Kasi learns the size itself and the user can decline in its dialog.

**Acknowledgement** of `addDownload` means durable acceptance (the job exists or the add dialog is queued), not the user's answer in a dialog, so the 2 s window is not tripped by a slow click.

## Consequences

- A fresh Kasi must push a populated default list, since an empty one intercepts nothing.
- The M2.8 `rules` sender must read `hello.extensionVersion` before adding `minSizeBytes`, and the `addDownload` handler must ack on acceptance.
- Later fields follow the same rule: optional, capped, sent only to peers that declared a version able to read them.
