import { rm } from 'node:fs/promises';
import { afterAll, beforeAll } from 'vitest';
import { defineRealScenarios } from './real-scenarios';
import {
  launchChromium,
  prepareChromiumExtension,
  type ChromiumExtension,
} from './support/chromium';
import { unavailableReason } from './support/harness';

let extension: ChromiumExtension | undefined;

beforeAll(async () => {
  if (unavailableReason() === undefined) extension = await prepareChromiumExtension();
});

afterAll(async () => {
  if (extension) await rm(extension.dir, { recursive: true, force: true });
});

defineRealScenarios({
  family: 'chromium',
  get allowed() {
    return extension?.id ?? 'not-prepared';
  },
  launch: async (profile) => {
    if (!extension) throw new Error('the extension was not prepared');
    return launchChromium(extension, profile);
  },
  unavailable: () => undefined,
});
