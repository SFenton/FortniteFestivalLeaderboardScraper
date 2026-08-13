import { expect, test } from '@playwright/test';

test('metric help owns focus above Rank By and restores both launchers', async ({ mount, page }) => {
  const component = await mount('pages/leaderboards/modals/RankByModal/MetricHelpFocusLifecycle');
  const launcher = component.getByRole('button', { name: 'Open Rank By' });
  await launcher.click();

  const rankBy = page.locator('[role="dialog"][aria-label="Rank By"]');
  const info = rankBy.getByRole('button', { name: 'Learn how FC Rate works' });
  await expect(rankBy).toBeVisible();
  const adjusted = rankBy.getByRole('button', { name: /^Adjusted Percentile/ });
  const adjustedBox = await adjusted.boundingBox();
  expect(adjustedBox).not.toBeNull();
  expect(adjustedBox!.height).toBeGreaterThanOrEqual(44);
  await page.mouse.click(adjustedBox!.x + 4, adjustedBox!.y + adjustedBox!.height / 2);
  await expect(adjusted).toHaveAttribute('aria-pressed', 'true');

  await info.click();

  const help = page.getByRole('dialog', { name: 'FC Rate details' });
  await expect(help).toBeVisible();
  await expect(rankBy).toHaveAttribute('inert', '');
  await expect(rankBy).toHaveAttribute('aria-hidden', 'true');
  await expect(help.getByRole('button', { name: 'Close' })).toBeFocused();

  await help.focus();
  await page.keyboard.press('Shift+Tab');
  await expect(help.getByRole('button', { name: 'Forward one entry' })).toBeFocused();
  await help.focus();
  await page.keyboard.press('Tab');
  await expect(help.getByRole('button', { name: 'Close' })).toBeFocused();

  await page.evaluate(() => {
    const state = window as Window & { __metricHelpArrowLeakCount?: number };
    state.__metricHelpArrowLeakCount = 0;
    window.addEventListener('keydown', event => {
      if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
        state.__metricHelpArrowLeakCount = (state.__metricHelpArrowLeakCount ?? 0) + 1;
      }
    });
  });
  await page.keyboard.press('ArrowRight');
  await expect(page.getByTestId('fre-title')).toContainText('Why Catalog Coverage Matters');
  await expect.poll(() => page.evaluate(() => (
    (window as Window & { __metricHelpArrowLeakCount?: number }).__metricHelpArrowLeakCount ?? 0
  ))).toBe(0);

  await page.keyboard.press('Escape');
  await expect(help).toBeHidden();
  await expect(rankBy).toBeVisible();
  await expect(info).toBeFocused();

  await rankBy.getByRole('button', { name: 'Close' }).click();
  await expect(rankBy).toBeHidden();
  await expect(launcher).toBeFocused();
});
