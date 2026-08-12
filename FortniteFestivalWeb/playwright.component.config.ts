import { defineConfig, devices } from '@playwright/test';

const componentPort = Number(process.env.PLAYWRIGHT_COMPONENT_PORT ?? 4175);
const galleryUrl = `http://localhost:${componentPort}/playwright/gallery/index.html`;

export default defineConfig({
  testDir: './component-tests',
  timeout: 30000,
  retries: process.env.CI ? 1 : 0,
  retryStrategy: 'isolated',
  failOnFlakyTests: Boolean(process.env.CI),
  reporter: process.env.CI
    ? [['blob'], ['line']]
    : [['list']],
  use: {
    baseURL: galleryUrl,
    headless: true,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'ct-chromium',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 800 },
      },
    },
    {
      name: 'ct-webkit',
      use: {
        ...devices['Desktop Safari'],
        viewport: { width: 1280, height: 800 },
      },
    },
    {
      name: 'ct-firefox',
      use: {
        ...devices['Desktop Firefox'],
        viewport: { width: 1280, height: 800 },
      },
    },
  ],
  webServer: {
    command: `npx vite --mode e2e --port ${componentPort}`,
    port: componentPort,
    reuseExistingServer: process.env.PLAYWRIGHT_REUSE_SERVER === '1',
    timeout: 60000,
  },
});
