import { defineConfig, devices } from '@playwright/test';

const chromiumExecutablePath = process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH;
const e2ePort = Number(process.env.PLAYWRIGHT_PORT ?? 3000);

export default defineConfig({
  testDir: './e2e',
  timeout: 30000,
  use: {
    baseURL: `http://localhost:${e2ePort}`,
    headless: true,
    ...(chromiumExecutablePath ? { launchOptions: { executablePath: chromiumExecutablePath, args: ['--no-sandbox'] } } : {}),
  },
  projects: [
    {
      name: 'wide-desktop',
      use: { viewport: { width: 1920, height: 1080 } },
    },
    {
      name: 'desktop-wide',
      use: { viewport: { width: 1440, height: 900 } },
    },
    {
      name: 'desktop',
      use: { viewport: { width: 1280, height: 800 } },
    },
    {
      name: 'desktop-narrow',
      use: { viewport: { width: 800, height: 800 } },
    },
    {
      name: 'mobile',
      use: { viewport: { width: 375, height: 812 }, hasTouch: true },
    },
    {
      name: 'mobile-narrow',
      use: { viewport: { width: 320, height: 568 }, hasTouch: true },
    },
  ],
  webServer: {
    command: `npx vite --mode e2e --port ${e2ePort}`,
    port: e2ePort,
    reuseExistingServer: true,
    timeout: 30000,
  },
});
