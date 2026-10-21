# Kasi browser extension

One TypeScript Manifest V3 codebase for Chrome, Edge and Firefox (KASI-NFR-015). Chrome and Edge load `dist/chromium`; Firefox loads `dist/firefox`. The background connects to the native host `com.martsoft.kasi` (`src/shared/native-port.ts`), caches pushed rules in `storage.local` and forwards the options page's `openSettings` request. Browser interception, menus and the picker arrive in M2.4 and M2.5.

## Build

Use Node 22.12 or newer (`.nvmrc` selects Node 22):

```powershell
cd extension
npm ci
npm run check:types
npm run lint
npm run format:check
npm test
npm run build
```

The build verifies both manifests and their packaged assets. `npm run build:chromium` and `npm run build:firefox` build one target for local work. Load the corresponding `dist` folder as an unpacked extension. The Firefox package has the Gecko ID `extension@kasi.martsoft.com`; Chromium does not include Firefox-only keys.

## Protocol

`docs/protocol/kasi-messages.schema.json` is the message source of truth (KASI-NFR-010). Run `npm run generate:types` after changing it and commit `src/types/protocol.ts`. `npm run check:types` fails if that file drifts. The options page sends a typed `openSettings` request through the background; the popup and options show "not connected" until a host answers. Incoming messages are checked at runtime by `src/shared/messages.ts` (Ajv cannot run inside an extension: its `new Function` is blocked by the MV3 content security policy), and `tests/unit/fixtures.test.ts` proves those checks and the schema agree with `tests/fixtures/ipc`.

## Behaviour

- **Native port:** reconnects with a 1 s to 30 s doubling backoff, sends `hello` on every connect, and matches `ack`/`error` replies by id with a 2 s timeout (Kasi's `rules.ackTimeoutMs` replaces it).
- **Logging:** `src/shared/logger.ts` redacts query strings, cookies, headers, tokens, error stacks and Windows paths. Use it instead of `console`.
- **Firefox permissions:** Firefox lets users deny or revoke `<all_urls>`, so the popup and options show "needs permission" with an **Allow Kasi on all sites** button until it is granted.
- **Firefox data consent:** the manifest declares `data_collection_permissions: { required: ["none"] }` (see ADR 0018), which is why `strict_min_version` is 140.

## Tests

`npm test` runs Vitest (`tests/unit`). No test contacts the public internet. The browser smoke runs nightly (`.github/workflows/nightly-extension-smoke.yml`) or by hand:

```powershell
npm run build
npx playwright install chromium
npm run smoke:chromium   # popup shows the not-connected state
npm run smoke:firefox    # web-ext lint (AMO validator) on dist/firefox
```

Playwright cannot load Firefox extensions, so the Firefox popup is not driven yet; that needs geckodriver and arrives with the registered host in M2.4.
