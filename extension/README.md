# Kasi browser extension

One TypeScript Manifest V3 codebase for Chrome, Edge and Firefox (KASI-NFR-015). Chrome and Edge load `dist/chromium`; Firefox loads `dist/firefox`. The background connects to the native host `com.martsoft.kasi` (`src/shared/native-port.ts`), caches pushed rules in `storage.local` and forwards the options page's `openSettings` request. Download interception (M2.4b) is described under Behaviour; menus and the picker arrive in M2.5.

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

The build verifies both manifests and their packaged assets. `npm run build:chromium` and `npm run build:firefox` build one target for local work. Load the corresponding `dist` folder as an unpacked extension. The Firefox package has the Gecko ID `kasi@martsoft.co.ke`; the store IDs of all three browsers are in `store-ids.json`, which the native host registration allow-lists (ADR 0017); Chromium does not include Firefox-only keys.

## Protocol

`docs/protocol/kasi-messages.schema.json` is the message source of truth (KASI-NFR-010). Run `npm run generate:types` after changing it and commit `src/types/protocol.ts`. `npm run check:types` fails if that file drifts. The options page sends a typed `openSettings` request through the background; the popup and options show "not connected" until a host answers. Incoming messages are checked at runtime by `src/shared/messages.ts` (Ajv cannot run inside an extension: its `new Function` is blocked by the MV3 content security policy), and `tests/unit/fixtures.test.ts` proves those checks and the schema agree with `tests/fixtures/ipc`.

## Behaviour

- **Native port:** reconnects with a 1 s to 30 s doubling backoff while it has never been connected; after a _lost_ connection (Kasi quit: the host sends `APP_NOT_RUNNING` and exits) it shows `!` at once and reconnects only for a matching download, a browser start or the popup being opened (not after a quit). Sends `hello` on every connect, and matches `ack`/`error` replies by id with a 2 s timeout (Kasi's `rules.ackTimeoutMs` replaces it).
- **Interception (KASI-BRW-002, 003, 006):** `downloads.onCreated` pauses a matching download (`src/shared/interception.ts`), reads cookies for the URL and the final URL only after the match, sends `addDownload`, and cancels then erases the browser download only after Kasi's ack. Any other outcome resumes it and shows a red `!` badge until Kasi sends `rules` again. When the port is not connected the extension waits at most 6 s for Kasi to start (the host launches it) and then up to `rules.ackTimeoutMs` (default 2 s) for the ack; with no host registered or Kasi not installed nothing is waited for. Rules come from the last `rules` message, else the `storage.local` cache, so a freshly started worker decides the same way. An ack that arrives after the timeout cancels the browser download if it is still running.
- **Context menus (M2.5b):** "Download with Kasi" on links and media, "Download all links" and "Download selected links" on pages (`src/shared/menus.ts`, `link-collector.ts`). The links of every frame come from `scripting.executeScript`; only http, https and ftp links are kept, without duplicates, at most 100,000 and 32 MiB per batch. A menu click never pauses a download and does not depend on the toggle below.
- **Toggle (M2.5a):** the popup checkbox stores `interceptionEnabled` in `storage.local` (default on); off intercepts nothing and the badge shows `off`. The popup re-reads the connection state every second while open.
- **Never intercepted:** `blob:`, `data:`, `file:` and other non-web schemes, URLs over 8,192 characters, another extension's downloads, POST-form downloads (including `POST` then redirect to `GET`; `webRequest` marks, kept 30 s as hashes in `storage.session`), private windows unless the user allowed the extension there, excluded sites, and everything when the host permission is revoked.
- **Bypass key:** a content script reports trusted clicks made while Alt, Ctrl or Shift is held; the background keeps the last one (as a hash of the link) and, at a match, lets the browser keep the download when the configured key was held on that link (15 s) or anywhere (3 s). **Limit:** a download that starts outside a page (address bar, bookmark, the browser's own "Save link as", another application, restricted pages such as `chrome://` and the Web Store, or Firefox without the host permission) has no click to read, so it cannot be bypassed with a key; use excluded sites for those.
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

The end-to-end suites (M2.4c, `TC-BRW-001` to `004`, and `TC-BRW-007`, the allow-list in `tests/e2e/allowlist.e2e.test.ts`) drive a real browser with the real Native AOT host and a stub Kasi (`tests/Kasi.BrowserHarness`); see ADR 0019. They need Windows and register the native host under HKCU for the browser under test (the previous value is restored), so they run only with `KASI_E2E=1`; without it, or without the pieces below, each suite is skipped with the reason printed.

```powershell
dotnet publish src/Kasi.NativeHost -c Release -r win-x64           # Native AOT needs the VS C++ tools, with vswhere on PATH
dotnet build tests/Kasi.BrowserHarness -c Release
npm run build; npx playwright install chromium
$env:KASI_E2E = '1'                                                # KASI_NATIVEHOST_AOT / KASI_HARNESS_DIR override the default paths
npm run test:e2e -- chromium
npm run test:e2e -- allowlist -t chromium  # TC-BRW-007 for one browser; use -t firefox for the other
npm run test:e2e -- firefox     # needs geckodriver on PATH (or GECKODRIVER) and Firefox
```

CI: `.github/workflows/extension-e2e.yml` runs Chromium on pull requests that touch the extension, the protocol, the host or the harness, and Chromium plus Firefox nightly. Interception unit tests are titled `[TC-BRW-001]` to `[TC-BRW-004]`: `npx vitest run -t "TC-BRW-003"`. Not automated: branded Chrome and Edge (they ignore `--load-extension`), private windows, the Firefox toolbar badge and a revoked Firefox host permission; `docs/qa/browser-interception.md` lists them for the release-candidate run.
