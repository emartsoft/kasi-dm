import { it } from 'vitest';
import { defineScenarios } from './scenarios';
import { FIREFOX_ADDON_ID, firefoxUnavailable, launchFirefox } from './support/firefox';

defineScenarios({
  family: 'firefox',
  allowed: FIREFOX_ADDON_ID,
  launch: (profile) => launchFirefox(profile),
  unavailable: firefoxUnavailable,
});

// Not feasible through WebDriver: it cannot take <all_urls> away from an installed add-on. The decision is covered by the unit suite
// ([TC-BRW-004] "missing host permission" in tests/unit/exclusions.test.ts); the popup text for it is checked by hand (docs/qa).
it.skip('[TC-BRW-002] the browser keeps the download when the host permission was revoked (needs a manual step)', () =>
  undefined);
