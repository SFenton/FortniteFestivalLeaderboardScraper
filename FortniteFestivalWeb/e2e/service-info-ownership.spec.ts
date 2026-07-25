import { expect, test } from '@playwright/test';

const serviceInfo = {
  lastCompletedUpdate: {
    startedAt: '2026-07-13T07:23:04Z',
    completedAt: '2026-07-13T13:36:31Z',
  },
  currentUpdate: {
    status: 'failed',
    startedAt: '2026-07-16T03:47:06Z',
    phase: 'PostScrapeEnrichment',
    subOperation: 'failed',
    progressPercent: null,
    elapsedSeconds: null,
    estimatedRemainingSeconds: null,
    branches: null,
  },
  workerStatus: {
    status: 'offline',
    currentOperation: null,
    lastOperation: null,
    lastHeartbeatAt: '2026-07-16T17:17:01Z',
  },
  nextScheduledUpdateAt: null,
};

test('one shared request owns cold Settings, route reuse, and polling', async ({ page }) => {
  const requestStarts: number[] = [];
  let inFlight = 0;
  let maxInFlight = 0;

  await page.route('**/api/service-info', async route => {
    requestStarts.push(Date.now());
    inFlight += 1;
    maxInFlight = Math.max(maxInFlight, inFlight);
    await new Promise(resolve => setTimeout(resolve, 150));
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(serviceInfo),
    });
    inFlight -= 1;
  });

  await page.goto('/#/settings', { waitUntil: 'domcontentloaded' });
  await page.locator('[data-testid="settings-service-info-list"]').waitFor({ state: 'visible' });
  await page.waitForTimeout(250);
  expect(requestStarts).toHaveLength(1);

  await page.evaluate(() => {
    window.location.hash = '#/songs';
  });
  await page.waitForTimeout(200);
  await page.evaluate(() => {
    window.location.hash = '#/settings';
  });
  await page.locator('[data-testid="settings-service-info-list"]').waitFor({ state: 'visible' });
  await page.waitForTimeout(200);
  expect(requestStarts).toHaveLength(1);

  await expect.poll(() => requestStarts.length, { timeout: 7_000 }).toBe(2);
  await expect.poll(() => requestStarts.length, { timeout: 7_000 }).toBe(3);

  expect(maxInFlight).toBe(1);
  for (let index = 1; index < requestStarts.length; index += 1) {
    expect(requestStarts[index] - requestStarts[index - 1]).toBeGreaterThanOrEqual(4_500);
    expect(requestStarts[index] - requestStarts[index - 1]).toBeLessThan(6_500);
  }
});
