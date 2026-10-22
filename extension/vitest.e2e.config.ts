import { defineConfig } from 'vitest/config';

// End-to-end tests (M2.4c): a real browser, the real native host and the stub Kasi. They need Windows, KASI_E2E=1 and the
// published host and harness (see extension/README.md); without them every suite is skipped with a printed reason.
// One file at a time: there is one Kasi pipe per user, and a native host registration per browser family.
export default defineConfig({
  test: {
    include: ['tests/e2e/**/*.e2e.test.ts'],
    fileParallelism: false,
    testTimeout: 90_000,
    hookTimeout: 120_000,
  },
});
