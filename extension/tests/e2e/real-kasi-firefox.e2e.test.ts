import { defineRealScenarios } from './real-scenarios';
import { FIREFOX_ADDON_ID, firefoxUnavailable, launchFirefox } from './support/firefox';

defineRealScenarios({
  family: 'firefox',
  size: 30_000_000,
  allowed: FIREFOX_ADDON_ID,
  launch: (profile) => launchFirefox(profile),
  unavailable: firefoxUnavailable,
});
