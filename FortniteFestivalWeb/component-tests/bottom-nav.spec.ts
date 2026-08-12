import { test, expect } from '@playwright/test';

test('active tab is semantic, readable, and responsive at the split boundary', async ({
  mount,
  page,
}) => {
  await page.setViewportSize({ width: 599, height: 800 });
  const component = await mount('components/shell/mobile/BottomNav/PlayerNavigation');

  const settings = component.getByRole('button', { name: 'Settings' });
  await expect(settings).toHaveAttribute('aria-current', 'page');
  await expect(settings).toHaveCSS('color', 'rgb(76, 125, 255)');
  await expect(component.getByRole('button', { name: 'Compete' })).toBeVisible();
  await expect(component.getByRole('button', { name: 'Leaderboards' })).toHaveCount(0);

  await page.setViewportSize({ width: 600, height: 800 });
  await expect(component.getByRole('button', { name: 'Leaderboards' })).toBeVisible();
  await expect(component.getByRole('button', { name: 'Rivals' })).toBeVisible();

  await component.getByRole('button', { name: 'Songs' }).click();
  await expect(component.getByTestId('active-tab')).toHaveValue('songs');
});
