import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';

test.use({ scenario: createPopulatedScenario() });

test('Songs and a modal remain usable in the current browser engine', async ({ page, appState }) => {
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, '/songs');

  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  const search = page.getByRole('button', { name: 'Search', exact: true }).first();
  await search.click();
  await expect(page.getByRole('dialog', { name: 'Search' })).toBeVisible();
  await page.keyboard.press('Escape');
  await dismissObstructions(page);
});

test('leaderboard rows render without horizontal overflow', async ({ page, appState }) => {
  await appState.reset();
  await appState.selectPlayer();
  await gotoAppRoute(page, '/songs/e2e-song-01/Solo_Guitar');

  await expect(page.getByText('Score Player 1', { exact: true })).toBeVisible();
  const overflow = await page.locator('#main-content').evaluate(element => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }));
  expect(overflow.scrollWidth).toBeLessThanOrEqual(overflow.clientWidth + 1);
});
