import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  timeout: 30000,
  use: {
    baseURL: 'http://localhost:3000',
    headless: true,
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
    command: 'npx vite --mode e2e --port 3000',
    port: 3000,
    reuseExistingServer: true,
    timeout: 30000,
  },
});
