# 0018. Browser extension scaffold and dual Manifest V3 builds

- Status: Accepted (M2.3a scaffold, M2.3b behaviour)
- Date: 2026-10-21
- Requirements: KASI-NFR-015, KASI-NFR-010
- Related: TRD 5.2 and 9.1–9.4; TPL 5.3; `docs/plans/m2-3.md`

## Context

Kasi needs one extension codebase for Chrome, Edge and Firefox. Chromium runs an MV3 background service worker; Firefox runs an MV3 event page. The extension messages must follow the M2.1a schema exactly, and the Chromium and Firefox packages must remain independently loadable.

## Decision

1. `extension/vite.config.ts` builds each target from the same TypeScript source. Popup and options are HTML entries; background and content are separate classic IIFE bundles. The build merges a shared manifest with one browser-specific manifest, so Chrome and Edge receive only `background.service_worker`, while Firefox receives only `background.scripts` and a stable Gecko add-on ID. Packaged assets and manifest invariants are checked after the dual build.
2. The Chromium minimum is Chrome 120. MV3 itself is older, but Chrome's service worker lifetime improvements through 120 give the native port work in M2.3b a consistent baseline. The Firefox minimum is 140.0 (ESR 140). It was 128.0 in M2.3a; 140 is the first Firefox that reads `data_collection_permissions` (decision 5), and `web-ext lint` warns for a lower floor. Firefox 127 and later show MV3 host permissions during install. Both floors include the supported browser versions in KASI-NFR-015. Firefox can still require a later explicit host permission grant after updates or revocation, handled in M2.3b/M2.4. See [Chrome's service worker lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle), [Firefox's background manifest](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/background) and [Firefox host permissions](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/host_permissions).
3. `docs/protocol/kasi-messages.schema.json` remains the sole protocol definition. `generate:types` writes `src/types/protocol.ts`; `check:types` compares regenerated text with the committed file and fails on drift. The options page constructs an `openSettings` request with the generated type and sends it to the background. The background listener forwards it through the native port manager; the page reports disconnected until an `ack` arrives.
4. TypeScript strict mode, ESLint 9 with typescript-eslint strict rules, Prettier, Vite, Vitest and Playwright are pinned in `package-lock.json` and run with Node 22. M2.3b adds the shared Ajv fixture suite, unit tests, a Windows CI job and a nightly browser smoke.
5. **Firefox data consent.** AMO requires `browser_specific_settings.gecko.data_collection_permissions` for new extensions submitted from 3 November 2025, with `required: ["none"]` for an extension that collects nothing ([MDN](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/browser_specific_settings); checked 2026-10-21). The Firefox manifest declares `none`: URLs and cookies go only to the user's own Kasi application through native messaging and are never sent to MartSoft. MDN does not say whether native messaging counts as transmission; the Extension Workshop's [built-in consent](https://extensionworkshop.com/documentation/develop/firefox-builtin-data-consent/) page should be re-read at the first AMO submission. Key support starts at Firefox 140 desktop and 142 Android, so the manifest also sets `gecko_android.strict_min_version` to 142.0.
6. **Runtime validation without Ajv.** Ajv compiles schemas with `new Function`, which the MV3 content security policy forbids, so the extension validates messages from Kasi with small hand-written checks (`src/shared/messages.ts`). Ajv stays a dev dependency: the fixture suite validates every file in `tests/fixtures/ipc` against the schema and also runs the hand-written checks on the `rules`, `ack` and `error` fixtures, so the two cannot drift. The schema cannot compare `protocolMax` with `protocolMin`, so the suite checks that rule itself, as the C# parser does.
7. **Native port.** `connectNative` is wrapped by `createNativePort`: backoff 1 s doubling to 30 s (reset by any valid message), `hello` on every connect, `ack`/`error` matched by id, 2 s ack timeout, pending requests rejected on disconnect. A redacting logger is the only logging path (CLAUDE.md rule 3).
8. **Browser smoke.** Nightly or manual, not PR CI. Chromium is driven by Playwright; Playwright cannot load Firefox extensions, so Firefox gets `web-ext lint` (AMO validator) until geckodriver-based popup checks arrive with M2.4.

## Dependencies and licences

The added npm packages were checked on npm when pinned: TypeScript and Playwright are Apache-2.0; `webextension-polyfill` is MPL-2.0; Vite, Vitest, ESLint, `@eslint/js`, typescript-eslint, Prettier, `json-schema-to-typescript`, Ajv, `tsx`, `@types/node` and `@types/webextension-polyfill` are MIT. The polyfill is the only runtime dependency. `web-ext` (MPL-2.0, development only, not shipped) was approved by the owner for the Firefox smoke; its transitive tree reports no vulnerabilities in `npm audit --omit=dev`.

## Consequences

- No browser-specific branch is needed for features built on the scaffold.
- A manifest cannot point to a missing bundle or locale without failing the build check.
- The generated types are compile-time contracts; incoming messages are validated at runtime by `messages.ts`, which must be kept in step with the schema (the fixture suite fails when it is not).
- End-to-end behaviour against a registered host is verified in M2.4 and M2.5.
