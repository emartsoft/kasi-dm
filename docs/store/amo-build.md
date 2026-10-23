# Building the Firefox add-on from source

For Mozilla's reviewers (AMO requires the source of a bundled extension and the steps to rebuild it). The source package contains the `extension/` folder and `docs/protocol/` (the message schema the types are generated from).

## Environment

- Windows, macOS or Linux; Node.js from `extension/.nvmrc` (22.x; `package.json` allows 22.12 up to 24).
- npm 10 or newer. No other tools.

## Steps

```text
cd extension
npm ci
npm run build:firefox
```

The result is in `extension/dist/firefox` and is what `npm run package` zips as `kasi-extension-firefox-<version>.zip`. To confirm it matches the uploaded add-on, unzip the uploaded add-on and compare it with `dist/firefox` file by file.

## What the build does

`vite.config.ts` bundles `src/popup`, `src/options` (as modules) and `src/background`, `src/content` (as classic scripts) with Vite, copies `public/` (icons and `_locales`) and writes `manifest.json` from `manifests/manifest.base.json` plus `manifests/manifest.firefox.json`. Dependencies are the lock-file versions; the only run-time dependency is `webextension-polyfill`. Nothing is minified beyond Vite's production defaults and no source maps are produced.
