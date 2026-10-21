# 0018. Browser extension scaffold and dual Manifest V3 builds

- Status: Accepted for M2.3a; native messaging and browser verification continue in M2.3b
- Date: 2026-10-21
- Requirements: KASI-NFR-015, KASI-NFR-010
- Related: TRD 5.2 and 9.1–9.4; TPL 5.3; `docs/plans/m2-3.md`

## Context

Kasi needs one extension codebase for Chrome, Edge and Firefox. Chromium runs an MV3 background service worker; Firefox runs an MV3 event page. The extension messages must follow the M2.1a schema exactly, and the Chromium and Firefox packages must remain independently loadable.

## Decision

1. `extension/vite.config.ts` builds each target from the same TypeScript source. Popup and options are HTML entries; background and content are separate classic IIFE bundles. The build merges a shared manifest with one browser-specific manifest, so Chrome and Edge receive only `background.service_worker`, while Firefox receives only `background.scripts` and a stable Gecko add-on ID. Packaged assets and manifest invariants are checked after the dual build.
2. The Chromium minimum is Chrome 120. MV3 itself is older, but Chrome's service worker lifetime improvements through 120 give the native port work in M2.3b a consistent baseline. The Firefox minimum is 128.0, the first ESR after Firefox began showing MV3 host permissions during install in 127. Both floors include the supported browser versions in KASI-NFR-015. Firefox can still require a later explicit host permission grant after updates or revocation, handled in M2.3b/M2.4. See [Chrome's service worker lifecycle](https://developer.chrome.com/docs/extensions/develop/concepts/service-workers/lifecycle), [Firefox's background manifest](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/background) and [Firefox host permissions](https://developer.mozilla.org/en-US/docs/Mozilla/Add-ons/WebExtensions/manifest.json/host_permissions).
3. `docs/protocol/kasi-messages.schema.json` remains the sole protocol definition. `generate:types` writes `src/types/protocol.ts`; `check:types` compares regenerated text with the committed file and fails on drift. The options page constructs an `openSettings` request with the generated type and sends it to the background. M2.3b adds the background listener and native port manager; the page reports disconnected until an `ack` arrives.
4. TypeScript strict mode, ESLint 9 with typescript-eslint strict rules, Prettier, Vite, Vitest and Playwright are pinned in `package-lock.json` and run with Node 22. The M2.3a build has no browser behaviour tests; M2.3b adds the shared Ajv fixture suite, unit tests, CI job and browser smoke harness.

## Dependencies and licences

The added npm packages were checked on npm when pinned: TypeScript and Playwright are Apache-2.0; `webextension-polyfill` is MPL-2.0; Vite, Vitest, ESLint, `@eslint/js`, typescript-eslint, Prettier, `json-schema-to-typescript`, Ajv, `tsx`, `@types/node` and `@types/webextension-polyfill` are MIT. The polyfill is the only runtime dependency.

## Consequences

- No browser-specific branch is needed for features built on the scaffold.
- A manifest cannot point to a missing bundle or locale without failing the build check.
- The generated types are compile-time contracts; untrusted incoming messages still need runtime validation in M2.3b.
- The extension is a scaffold until the native host and browser smoke work are complete.
