import axe, { type AxeResults } from 'axe-core';
import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';
import { gotoAppRoute } from '../../support/drivers/app';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

test.use({ scenario: createPopulatedScenario() });

for (const route of ['/songs', '/leaderboards', '/settings', '/manual']) {
  test(`${route} has no serious or critical axe violations`, async ({ page, appState }) => {
    await appState.reset();
    await appState.selectPlayer();
    await gotoAppRoute(page, route);
    await page.addScriptTag({ content: axe.source });

    const results = await page.evaluate(() => window.axe.run());
    expect(results.violations.filter(violation => (
      violation.impact === 'serious' || violation.impact === 'critical'
    ))).toEqual([]);
  });
}

test('decorative background stops animating for reduced motion', async ({ page, appState }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await appState.reset();
  await gotoAppRoute(page, '/songs');
  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();

  const backgroundAnimations = await page.evaluate(() => (
    Array.from(document.querySelectorAll<HTMLElement>('div'))
      .filter(element => getComputedStyle(element).backgroundImage !== 'none')
      .reduce((count, element) => count + element.getAnimations().length, 0)
  ));
  expect(backgroundAnimations).toBe(0);
});
