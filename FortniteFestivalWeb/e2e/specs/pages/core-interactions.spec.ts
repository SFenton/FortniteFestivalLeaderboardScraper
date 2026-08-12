import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario, E2E_SONG_ID } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';
import { isMobileProject } from '../../support/projects';

test.use({ scenario: createPopulatedScenario() });

test.beforeEach(async ({ appState }) => {
  await appState.reset();
  await appState.selectPlayer();
});

test('Settings toggles persist through browser storage', async ({ page }) => {
  await gotoAppRoute(page, '/settings');
  const filterInvalid = page.getByRole('button', { name: /Filter Invalid Scores/ }).first();
  await expect(filterInvalid).toBeVisible();
  await filterInvalid.click();

  await expect.poll(async () => {
    const raw = await page.localStorage.getItem('fst:appSettings');
    return raw ? JSON.parse(raw).filterInvalidScores : null;
  }).toBe(true);
});

test('Suggestions filter opens with populated player data', async ({ page }, testInfo) => {
  await gotoAppRoute(page, '/suggestions');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);

  const filter = page.getByRole('button', {
    name: isMobileProject(testInfo.project.name) ? 'Filter Suggestions' : 'Filter',
    exact: true,
  }).first();
  await filter.click();
  await expect(page.getByText('Filter Suggestions', { exact: true })).toBeVisible();
  await page.keyboard.press('Escape');
});

test('Song detail paths and chart controls are browser-interactive', async ({ page, appState }) => {
  await appState.setSettings({ pathUnavailableWarningDismissed: true });
  await gotoAppRoute(page, `/songs/${E2E_SONG_ID}`);
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);

  const chartPath = page.locator('[aria-label^="Accuracy:"]').first();
  await expect(chartPath).toBeVisible();
  await chartPath.focus();
  await chartPath.press('Enter');

  const paths = page.getByRole('button', { name: 'View Paths', exact: true }).first();
  await paths.click();
  const dialog = page.getByRole('dialog', { name: 'Paths' });
  await expect(dialog).toBeVisible();
  await expect(dialog.locator('img, svg').first()).toBeVisible();
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();
});

test('license cards open and close their detail dialog', async ({ page }) => {
  await gotoAppRoute(page, '/settings/licenses');

  await page.getByText('@playwright/test', { exact: true }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog).toContainText('@playwright/test');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
});
