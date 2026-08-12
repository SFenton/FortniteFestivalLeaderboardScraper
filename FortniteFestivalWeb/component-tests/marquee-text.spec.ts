import { test, expect } from '@playwright/test';

test('long text overflows at constrained widths', async ({ mount, page }) => {
  const component = await mount('components/common/MarqueeText/LongText', { width: 180 });
  await expect(component.locator('[aria-hidden="true"]')).toBeVisible();
  await page.setViewportSize({ width: 320, height: 568 });
  await expect(component).toBeVisible();
});
