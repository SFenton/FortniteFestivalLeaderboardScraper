import type { Page } from '@playwright/test';
import { expect, test } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';
import { isPrimaryDesktopProject } from '../../support/projects';

const METRIC_INFO_MODULE = '/pages/leaderboards/firstRun/metricInfo/MetricInfoCarousel.tsx';

test.use({ scenario: createPopulatedScenario() });

test.beforeEach(async ({ appState }) => {
  await appState.reset();
  await appState.selectPlayer();
  await appState.setSettings({ enableExperimentalRanks: true });
});

test('Rank By defers metric help and KaTeX until the info action', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'metric-help network ownership is covered once');
  const requests = trackModuleRequests(page);
  const releaseMetricInfo = await holdFirstRoute(page, `**/src${METRIC_INFO_MODULE}*`);

  await gotoAppRoute(page, '/leaderboards');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  expect(hasMetricPayload(requests)).toBe(false);

  await page.locator('button[aria-label="Total Score"]').first().click();
  const rankBy = page.locator('[role="dialog"][aria-label="Rank By"]');
  await expect(rankBy).toBeVisible();
  await rankBy.getByRole('button', { name: /^Popularity-Weighted Percentile/ }).click();
  expect(hasMetricPayload(requests)).toBe(false);

  const info = rankBy.getByRole('button', { name: 'Learn how Popularity-Weighted Percentile works' });
  await info.click();
  const loading = page.getByTestId('rank-metric-info-lazy-loading');
  try {
    await expect(loading).toBeVisible();
    await expect(loading).toBeFocused();
    await page.keyboard.press('Shift+Tab');
    await expect(loading.getByRole('button', { name: 'Close' })).toBeFocused();
    expect(requests.some(url => url.includes('/node_modules/katex/'))).toBe(false);
  } finally {
    releaseMetricInfo();
  }

  const help = page.getByRole('dialog', { name: 'Popularity-Weighted Percentile details' });
  await expect(help).toBeVisible();
  await expect(rankBy).toHaveAttribute('inert', '');
  await expect.poll(() => requests.some(url => url.includes('/node_modules/katex/'))).toBe(true);
  expect(requests.filter(url => url.includes(METRIC_INFO_MODULE))).toHaveLength(1);
  expect(requests.some(url => url.includes('/fonts/KaTeX_'))).toBe(false);

  const forward = help.getByRole('button', { name: 'Forward one entry' });
  await forward.click();
  await expect(page.getByTestId('fre-title')).toContainText('Why Score Count Matters');
  await forward.click();
  await expect(page.locator('.katex-display')).toBeVisible();
  await expect.poll(() => requests.some(url => url.includes('/fonts/KaTeX_'))).toBe(true);

  await page.keyboard.press('Escape');
  await expect(help).toBeHidden();
  await expect(rankBy).toBeVisible();
  await expect(info).toBeFocused();

  await info.click();
  await expect(help).toBeVisible();
  expect(requests.filter(url => url.includes(METRIC_INFO_MODULE))).toHaveLength(1);
});

test('metric-help chunk failure stays fail-closed and returns focus', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'metric-help failure behavior is covered once');
  await page.route(`**/src${METRIC_INFO_MODULE}*`, route => route.abort('failed'), { times: 1 });
  await gotoAppRoute(page, '/leaderboards');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  await page.locator('button[aria-label="Total Score"]').first().click();

  const rankBy = page.locator('[role="dialog"][aria-label="Rank By"]');
  const info = rankBy.getByRole('button', { name: 'Learn how FC Rate works' });
  await info.click();
  const failure = page.getByTestId('rank-metric-info-lazy-error');
  await expect(failure).toBeVisible();
  await expect(failure.getByRole('alert')).toContainText('could not be loaded');
  await failure.getByRole('button', { name: 'Close' }).click();
  await expect(failure).toBeHidden();
  await expect(rankBy).toBeVisible();
  await expect(info).toBeFocused();
});

test('band Rank By exposes no player metric-info actions', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'band suppression is covered once');
  const requests = trackModuleRequests(page);
  await gotoAppRoute(page, '/leaderboards/bands/Band_Duets');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  await page.locator('button[aria-label="Total Score"]').first().click();

  const rankBy = page.locator('[role="dialog"][aria-label="Rank By"]');
  await expect(rankBy).toBeVisible();
  await expect(rankBy.getByRole('button', { name: /Learn how .* works/ })).toHaveCount(0);
  expect(hasMetricPayload(requests)).toBe(false);
});

test('aggregate player Rank By uses scoped copy without instrument metric help', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'aggregate metric-help suppression is covered once');
  const requests = trackModuleRequests(page);
  for (const route of [
    '/leaderboards/all?combo=03',
    '/leaderboards/all?family=pad',
  ]) {
    await gotoAppRoute(page, route);
    await page.locator('button[aria-label="Total Score"]').first().click();

    const rankBy = page.locator('[role="dialog"][aria-label="Rank By"]');
    await expect(rankBy).toBeVisible();
    await expect(rankBy.getByRole('button', { name: /Learn how .* works/ })).toHaveCount(0);
    await rankBy.getByRole('button', { name: 'Close' }).click();
    await expect(rankBy).toBeHidden();
  }
  expect(hasMetricPayload(requests)).toBe(false);
});

function trackModuleRequests(page: Page) {
  const requests: string[] = [];
  page.on('request', request => {
    const url = request.url();
    if (url.includes('/src/') || url.includes('/node_modules/')) requests.push(url);
  });
  return requests;
}

function hasMetricPayload(requests: readonly string[]) {
  return requests.some(url => (
    url.includes(METRIC_INFO_MODULE)
    || url.includes('/pages/leaderboards/firstRun/metricInfo/index.ts')
    || url.includes('/components/common/Math.tsx')
    || url.includes('/node_modules/katex/')
  ));
}

async function holdFirstRoute(page: Page, url: string): Promise<() => void> {
  let release!: () => void;
  const gate = new Promise<void>(resolve => {
    release = resolve;
  });
  await page.route(url, async route => {
    await gate;
    await route.continue();
  }, { times: 1 });
  return release;
}
