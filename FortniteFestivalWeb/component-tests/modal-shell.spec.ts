import axe, { type AxeResults } from 'axe-core';
import { test, expect } from '@playwright/test';

declare global {
  interface Window {
    axe: {
      run(context?: string): Promise<AxeResults>;
    };
  }
}

test('modal traps focus, closes with Escape, and restores focus', async ({ mount, page }) => {
  const component = await mount('components/modals/ModalShell/FocusLifecycle');
  const launcher = component.getByRole('button', { name: 'Open test modal' });
  await launcher.focus();
  await launcher.press('Enter');

  const dialog = page.getByRole('dialog', { name: 'Component test modal' });
  await expect(dialog).toBeVisible();
  const close = dialog.getByRole('button', { name: 'Close' });
  const last = dialog.getByRole('button', { name: 'Last modal action' });
  await expect(close).toBeFocused();
  await close.press('Shift+Tab');
  await expect(last).toBeFocused();
  await last.press('Tab');
  await expect(close).toBeFocused();

  await page.addScriptTag({ content: axe.source });
  const results = await page.evaluate(() => window.axe.run('[role="dialog"]'));
  expect(results.violations.filter(violation => (
    violation.impact === 'serious' || violation.impact === 'critical'
  ))).toEqual([]);

  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(launcher).toBeFocused();
});

for (const viewport of [
  { width: 320, height: 568 },
  { width: 768, height: 400 },
  { width: 1280, height: 800 },
]) {
  test(`modal fits ${viewport.width}x${viewport.height}`, async ({ mount, page }) => {
    await page.setViewportSize(viewport);
    const component = await mount('components/modals/ModalShell/FocusLifecycle');
    await component.getByRole('button', { name: 'Open test modal' }).click();
    const dialog = page.getByRole('dialog', { name: 'Component test modal' });
    const box = await dialog.boundingBox();
    expect(box).not.toBeNull();
    expect(box!.width).toBeLessThanOrEqual(viewport.width);
    expect(box!.height).toBeLessThanOrEqual(viewport.height);
  });
}
