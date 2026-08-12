import axe, { type AxeResults } from 'axe-core';
import { expect, test } from '@playwright/test';
import { isPrimaryDesktopProject } from '../../support/projects';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

test('shared modal traps focus, restores focus, and has no serious axe violations', async ({ page }, testInfo) => {
  test.skip(!isPrimaryDesktopProject(testInfo.project.name), 'single desktop accessibility fixture');

  await page.route('**/api/service-info', async (route) => {
    await route.fulfill({
      json: {
        lastCompletedUpdate: null,
        currentUpdate: { status: 'idle', startedAt: null, phase: null, subOperation: null },
        nextScheduledUpdateAt: null,
      },
    });
  });
  await page.goto('/?modalA11yFixture=1');
  const viewportContent = await page.locator('meta[name="viewport"]').getAttribute('content');
  expect(viewportContent).not.toContain('maximum-scale');
  expect(viewportContent).not.toContain('user-scalable=no');

  const launcher = page.getByRole('button', { name: 'Launch accessible modal' });
  await launcher.focus();
  await expect(launcher).toBeFocused();
  await launcher.press('Enter');

  const dialog = page.getByRole('dialog', { name: 'Accessibility test modal' });
  await expect(dialog).toBeVisible();
  const closeButton = dialog.getByRole('button', { name: 'Close' });
  const lastAction = dialog.getByRole('button', { name: 'Last modal action' });
  await expect(closeButton).toBeFocused();
  await closeButton.press('Shift+Tab');
  await expect(lastAction).toBeFocused();
  await lastAction.press('Tab');
  await expect(closeButton).toBeFocused();
  await expect(page.locator('#root')).toHaveAttribute('inert', '');
  await expect(page.locator('body')).toHaveCSS('overflow', 'hidden');

  await page.addScriptTag({ content: axe.source });
  const results = await page.evaluate(() => window.axe.run('[role="dialog"]'));
  const seriousViolations = results.violations.filter(
    (violation) => violation.impact === 'serious' || violation.impact === 'critical',
  );
  expect(seriousViolations).toEqual([]);

  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(launcher).toBeFocused();
});
