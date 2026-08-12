import { defineConfig, devices } from '@playwright/test';

const publicationPort = Number(process.env.PLAYWRIGHT_PUBLICATION_PORT ?? 4182);

export default defineConfig({
  testDir: './e2e/specs/platform',
  testMatch: 'publication.spec.ts',
  timeout: 90000,
  retries: process.env.CI ? 1 : 0,
  retryStrategy: 'isolated',
  failOnFlakyTests: Boolean(process.env.CI),
  reporter: process.env.CI
    ? [['blob'], ['line']]
    : [['list']],
  use: {
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 800 },
    baseURL: `http://localhost:${publicationPort}`,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'publication-chromium' }],
  webServer: {
    command: `npx vite --mode e2e --port ${publicationPort}`,
    env: { VITE_FST_STUB_PUBLICATION: 'false' },
    port: publicationPort,
    reuseExistingServer: process.env.PLAYWRIGHT_REUSE_SERVER === '1',
    timeout: 60000,
  },
});
