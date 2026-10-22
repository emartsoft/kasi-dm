import { it } from 'vitest';
import { defineScenarios } from './scenarios';
import { FIREFOX_ADDON_ID, firefoxUnavailable, launchFirefox } from './support/firefox';

defineScenarios({
  family: 'firefox',
  size: 30_000_000,
  // Firefox drops a WebDriver-installed add-on's storage between sessions: the launch-chain case needs the rules cached by an earlier
  // session, so it runs in Chromium only (the unit suite covers the wait itself).
  rulesSurviveRestart: false,
  allowed: FIREFOX_ADDON_ID,
  launch: (profile) => launchFirefox(profile),
  unavailable: firefoxUnavailable,
});

// Not feasible through WebDriver: it cannot take <all_urls> away from an installed add-on. The decision is covered by the unit suite
// ([TC-BRW-004] "missing host permission" in tests/unit/exclusions.test.ts); the popup text for it is checked by hand (docs/qa).
it.skip('[TC-BRW-002] the browser keeps the download when the host permission was revoked (needs a manual step)', () =>
  undefined);
