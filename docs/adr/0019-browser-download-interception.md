# 0019. Browser download interception

- Status: Proposed (rule semantics in M2.4a; the hand-off budget and bypass detection in M2.4b; the end-to-end harness is added by M2.4c)
- Date: 2026-10-21
- Requirements: KASI-BRW-002, KASI-BRW-003, KASI-NFR-010
- Related: TRD 9.2 and 9.4; `docs/plans/m2-4.md`; ADR 0016, ADR 0018

## Context

`rules` carried extension, MIME and site lists with no stated semantics, and no size field although BRW-002 names a minimum size. Kasi and the extension must decide alike, and the strict parsers on both sides reject any field the other side does not know.

## Decision

**Rule semantics** are written into TRD 9.2 and `docs/protocol/README.md` and implemented as pure functions in `extension/src/shared/rules-match.ts`: a download is taken when rules exist and are enabled, it is not excluded, and its extension or its MIME type matches. Extensions are lowercase without a dot and match the end of the file name; MIME types are exact or `type/*`; an empty list matches nothing (never take what was not asked for); an excluded site covers its subdomains and is tested against the URL, the final URL and the referrer. `blob:`, `data:`, other schemes, URLs over 8,192 characters, another extension's downloads and private windows (unless allowed) are never taken.

**Minimum size** is an optional `minSizeBytes` (integer, 0 to 2^53-1) on `rules`. Because a strict parser rejects the whole message, Kasi sends it only when `hello.extensionVersion` is 0.2.0 or later (`IpcProtocol.MinSizeExtensionVersion`), and `v` stays 1. The extension is released as 0.2.0 with the field. A download of unknown size is intercepted; Kasi learns the size itself and the user can decline in its dialog.

**Acknowledgement** of `addDownload` means durable acceptance (the job exists or the add dialog is queued), not the user's answer in a dialog, so the 2 s window is not tripped by a slow click.

**Hand-off budget** (M2.4b, KASI-BRW-002, BRW-006). A matching download is paused first and then handed to Kasi in two phases. The *ready* phase applies only when the native port is not connected: the extension asks the port to reconnect now and waits at most 6 s (the host's 5 s launch window plus the `hello`/`rules` round trip) for Kasi's `rules`. The *ack* phase waits `rules.ackTimeoutMs` (default 2 s), counted from posting `addDownload`. A port is *connected* only after `rules`; the host's `APP_NOT_RUNNING` and `APP_NOT_INSTALLED` errors, and a port that dies before saying anything (no host registered), mean unreachable, and the last two skip the wait entirely so a machine without Kasi never slows a download. On an ack the extension cancels, removes the part file where the browser leaves one (Firefox) and erases the browser download; in every other case it resumes it (unless the user cancelled it meanwhile) and shows a red `!` badge until `rules` arrive again.

**Late ack.** An ack that arrives after the timeout, within 30 s, cancels and erases the browser download if it is still in progress, so the file is not downloaded twice; if its bytes are already complete nothing is done and Kasi's duplicate prompt (ENG-013) handles it. A request id is never retried.

**Bypass key.** `downloads.onCreated` cannot see the keyboard, so a content script (all frames, `document_start`) reports trusted pointer and Enter events with Alt, Ctrl or Shift held; the background keeps the last one with the SHA-256 of the link (never the link) and, after pausing, honours it when the key equals `rules.bypassKey` and it is fresh: 15 s when the link equals the download's URL, final URL or a redirect hop, otherwise 3 s, after a 100 ms grace when no record exists yet. Downloads that start outside a page cannot be bypassed by key; this is documented, not worked around (a native key-state query in the host was rejected: the key is usually released before `onCreated`).

**Exclusions** are decided before the pause: non-web schemes, over-long URLs, other extensions' downloads, private windows unless allowed, a missing host permission, and POST-form downloads, which `webRequest` marks because a `DownloadItem` has no method (the mark follows redirects, lasts 30 s, and is stored as hashes).

**Cookies** are read only after a match, for the URL and the final URL, with the cookie's domain passed unchanged (the leading dot is the only host-only signal), values over 4,096 characters dropped and at most 200 sent. They are held for one hand-off and never logged or stored.

## Consequences

- A fresh Kasi must push a populated default list, since an empty one intercepts nothing.
- The M2.8 `rules` sender must read `hello.extensionVersion` before adding `minSizeBytes`, and the `addDownload` handler must ack on acceptance.
- Later fields follow the same rule: optional, capped, sent only to peers that declared a version able to read them.
