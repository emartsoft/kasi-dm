# Kasi browser extension

One TypeScript Manifest V3 codebase for Chrome, Edge and Firefox (KASI-NFR-015). Chrome and Edge load `dist/chromium`; Firefox loads `dist/firefox`. This M2.3a scaffold packages the manifests, generated protocol types, icons, popup and options pages. Browser interception and the native port manager arrive in M2.3b–M2.5.

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

`docs/protocol/kasi-messages.schema.json` is the message source of truth (KASI-NFR-010). Run `npm run generate:types` after changing it and commit `src/types/protocol.ts`. `npm run check:types` fails if that file drifts. The options page builds a typed `openSettings` request and shows the disconnected state until the M2.3b background listener and native host can acknowledge it.

Vitest and Playwright are installed but have no browser behaviour cases in M2.3a. M2.3b adds Ajv tests against `tests/fixtures/ipc`, the native port manager tests and the browser smoke harness. No tests contact the public internet.
