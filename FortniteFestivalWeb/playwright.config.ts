import { defineConfig, devices } from '@playwright/test';

const chromiumExecutablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
const webkitLibraryPath = process.env.PLAYWRIGHT_WEBKIT_LD_LIBRARY_PATH;
const e2ePort = Number(process.env.PLAYWRIGHT_PORT ?? 4173);
const reuseExistingServer = process.env.PLAYWRIGHT_REUSE_SERVER === '1';
const crossEngineTests = [
  '**/specs/browser/**/*.spec.ts',
  '**/specs/accessibility/**/*.spec.ts',
];
const wideTests = [
  '**/specs/pages/manual/**/*.spec.ts',
  '**/specs/responsive/**/*.spec.ts',
];

export default defineConfig({
  testDir: './e2e',
  testIgnore: ['**/specs/platform/publication.spec.ts'],
  timeout: 90000,
  retries: process.env.CI ? 1 : 0,
  retryStrategy: 'isolated',
  failOnFlakyTests: Boolean(process.env.CI),
  reporter: process.env.CI
    ? [['blob'], ['line']]
    : [['list']],
  use: {
    baseURL: `http://localhost:${e2ePort}`,
    headless: true,
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    ...(chromiumExecutablePath ? { launchOptions: { executablePath: chromiumExecutablePath, args: ['--no-sandbox'] } } : {}),
  },
  projects: [
    {
      name: 'chromium-desktop',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1280, height: 800 },
      },
    },
    {
      name: 'chromium-mobile',
      use: {
        ...devices['Pixel 7'],
        viewport: { width: 390, height: 844 },
      },
    },
    {
      name: 'chromium-wide',
      testMatch: wideTests,
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 900 },
      },
    },
    {
      name: 'webkit-mobile',
      testMatch: crossEngineTests,
      use: {
        ...devices['iPhone 14'],
        ...(webkitLibraryPath ? { launchOptions: { env: { LD_LIBRARY_PATH: webkitLibraryPath } } } : {}),
      },
    },
    {
      name: 'webkit-desktop',
      testMatch: crossEngineTests,
      use: {
        ...devices['Desktop Safari'],
        viewport: { width: 1280, height: 800 },
        ...(webkitLibraryPath ? { launchOptions: { env: { LD_LIBRARY_PATH: webkitLibraryPath } } } : {}),
      },
    },
    {
      name: 'firefox-desktop',
      testMatch: crossEngineTests,
      use: {
        ...devices['Desktop Firefox'],
        viewport: { width: 1280, height: 800 },
      },
    },
  ],
  webServer: {
    command: `npx vite --mode e2e --port ${e2ePort}`,
    port: e2ePort,
    reuseExistingServer,
    timeout: 60000,
  },
});
