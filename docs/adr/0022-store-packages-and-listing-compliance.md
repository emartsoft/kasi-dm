# ADR 0022: Store packages and listing compliance

- Status: Proposed (M2.7)
- Requirements: KASI-BRW-001 (acceptance is "store listings approved"; this gets the packages to review), KASI-NFR-013 (privacy)
- Plan: `docs/plans/m2-7.md`

## Context

The extension must be reviewed by three stores before the alpha. A package a store rejects, a justification that does not match the code, or a missing privacy answer costs a review cycle. The desktop app that makes the extension useful is not public yet.

## Decision

**What a package is.** `npm run package` (`extension/scripts/package.ts`) builds both targets, then refuses to continue if a manifest has a `key`, an `update_url` or a local address, is not Manifest V3, or its version is not `package.json`'s; if the folder holds a source map, tests, `node_modules` or `.git`; or if the manifest is missing. It zips `dist/chromium` and `dist/firefox` with `web-ext build` (already a dev dependency; no zip library is added) into `dist/packages/kasi-extension-<target>-<version>.zip`, then reads each zip back (a small central-directory reader on `node:zlib`) and applies the same checks to what was written. `check-manifests` also verifies that each icon is a PNG of the size its manifest key claims. The checks live in `scripts/package-lib.ts` and are unit tested with zips built in memory.

**The source package for AMO** is `git archive` of `extension/`, `docs/protocol/` (the schema the types are generated from) and `docs/store/amo-build.md` at `HEAD`, so only tracked files can be in it, and `sourceZipProblems` requires the lock file, `.nvmrc`, the Vite config and the build steps and refuses `node_modules` and `dist`. Unzipping it into an empty folder and running `npm ci` and `npm run build:firefox` was checked to reproduce `dist/firefox` byte for byte. CI runs `npm run package -- --skip-build` after the build.

**Justifications match the code.** `docs/store/permission-justifications.md` has one `###` entry per manifest permission, one for `<all_urls>`, one for the content script and one for remote code, each naming the source file that uses it. `tests/unit/store-docs.test.ts` fails when the manifest and the document differ in either direction, when an entry names a file that does not exist, and when the listing's name or short description differs from the extension's own strings. Two statements of the first draft were wrong and are corrected: `webRequest` is not used to read response headers (it only remembers which requests were not GET, ADR 0019), and the content script does not answer link-collection requests (the links come from `scripting.executeScript`, ADR 0020); it only reports which modifier key was held on a trusted click.

**Early submission is unlisted** (Chrome and Edge unlisted visibility, AMO unlisted) and after M2.8b, with `docs/store/review-notes.md` telling reviewers that without the desktop app the popup says "Kasi is not connected" and the browser downloads normally. Listing text is English only. The privacy policy (`docs/legal/privacy-policy.md`) states what is read (addresses, cookies of a download being taken, the user agent), that it goes only to the local Kasi app, that nothing reaches MartSoft or any server, that Kasi keeps cookies DPAPI-encrypted per job and deletes them with it, and that there is no remote code. `docs/store/data-usage.md` holds the matching answers for each store's questionnaire.

**Assets** are generated, not drawn: `tests/e2e/store-assets.e2e.test.ts` (opt-in with `KASI_E2E=1 KASI_STORE_ASSETS=1`) photographs the real popup (connected and not connected, with the stub Kasi on the real pipe) and options page in Chromium and composes 1280x800 screenshots and the 440x280 and 1400x560 promo tiles from HTML, so they are exact in size and stay current.

## Consequences

- A new permission cannot ship without a justification; a package with a development key or a source map cannot be built.
- The privacy policy's contact address is the one thing left for the owner (a bracketed placeholder, tested to be the only one); the policy URL, the accounts and the submissions are owner steps (`docs/plans/m2-7.md` section 5).
- The generated tiles are plain (the icon, the name and one line on a blue background); a designed tile can replace them without changing anything else.
- The packages hold no development key, so the Chrome ID comes from the store; the harness's per-run key never reaches a package.
