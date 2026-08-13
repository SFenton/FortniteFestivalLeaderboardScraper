import { expect, test } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { dismissObstructions, gotoAppRoute } from '../../support/drivers/app';
import { isPrimaryMobileProject } from '../../support/projects';

test.use({ scenario: createPopulatedScenario() });

test.beforeEach(async ({ appState }) => {
  await appState.reset();
  await appState.selectPlayer();
});

test('Suggestions filter excludes collapsed controls from keyboard navigation', async ({ page }, testInfo) => {
  test.skip(!isPrimaryMobileProject(testInfo.project.name), 'mobile disclosure tab order is covered once');
  await gotoAppRoute(page, '/suggestions');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);
  await page.getByRole('button', { name: 'Filter Suggestions', exact: true }).click();

  const dialog = page.getByRole('dialog', { name: 'Filter Suggestions' });
  const instruments = dialog.getByRole('button', { name: /Instruments/ });
  const general = dialog.getByRole('button', { name: /General/ });
  await expect(instruments).toHaveAttribute('aria-expanded', 'false');
  await expect(general).toHaveAttribute('aria-expanded', 'false');

  await instruments.focus();
  await instruments.press('Tab');
  await expect(general).toBeFocused();

  await instruments.press('Enter');
  await instruments.press('Tab');
  await expect(dialog.getByRole('button', { name: /Lead/ }).first()).toBeFocused();
});

test('mobile BottomNav exposes only the committed route as current', async ({ page }, testInfo) => {
  test.skip(!isPrimaryMobileProject(testInfo.project.name), 'mobile current-page semantics are covered once');
  await gotoAppRoute(page, '/suggestions');
  await page.getByTestId('fre-overlay').waitFor({ state: 'visible', timeout: 3_000 }).catch(() => {});
  await dismissObstructions(page);

  const suggestions = page.getByTestId('bottom-nav-suggestions');
  const settings = page.getByTestId('bottom-nav-settings');
  await expect(suggestions).toHaveAttribute('aria-current', 'page');
  await expect(page.locator('[aria-current="page"]')).toHaveCount(1);

  await settings.dispatchEvent('pointerdown', { pointerType: 'mouse', button: 0 });
  await expect(suggestions).toHaveAttribute('aria-current', 'page');
  await expect(settings).not.toHaveAttribute('aria-current');
  await expect(page.locator('[aria-current="page"]')).toHaveCount(1);
});
