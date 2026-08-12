import { test, expect } from '@playwright/test';

test('ConfirmAlert traps focus and restores its launcher', async ({ mount, page }) => {
  const component = await mount('components/modals/ConfirmAlert/FocusLifecycle');
  const launcher = component.getByRole('button', { name: 'Open confirmation' });
  await launcher.focus();
  await launcher.press('Enter');

  const dialog = page.getByRole('alertdialog', { name: 'Confirm component action' });
  await expect(dialog).toBeVisible();
  const no = dialog.getByRole('button', { name: 'No' });
  const yes = dialog.getByRole('button', { name: 'Yes' });
  await expect(no).toBeFocused();
  await no.press('Shift+Tab');
  await expect(yes).toBeFocused();
  await yes.press('Tab');
  await expect(no).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(launcher).toBeFocused();
});

test('ChangelogModal exposes dialog semantics and restores focus', async ({ mount, page }) => {
  const component = await mount('components/modals/ChangelogModal/FocusLifecycle');
  const launcher = component.getByRole('button', { name: 'Open changelog' });
  await launcher.focus();
  await launcher.press('Enter');

  const dialog = page.getByRole('dialog', { name: /What's New/ });
  await expect(dialog).toBeVisible();
  await expect(dialog.getByRole('button', { name: 'Close' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(launcher).toBeFocused();
});

test('nested ConfirmAlert owns focus above ModalShell', async ({ mount, page }) => {
  const component = await mount('components/modals/ConfirmAlert/NestedInModal');
  await component.getByRole('button', { name: 'Open parent modal' }).click();
  const parent = page.locator('[role="dialog"][aria-label="Parent modal"]');
  const nestedLauncher = parent.getByRole('button', { name: 'Open nested confirmation' });
  await nestedLauncher.click();

  const alert = page.getByRole('alertdialog', { name: 'Nested confirmation' });
  const no = alert.getByRole('button', { name: 'No' });
  const yes = alert.getByRole('button', { name: 'Yes' });
  await expect(no).toBeFocused();
  await expect(parent).toHaveAttribute('inert', '');
  await expect(parent).toHaveAttribute('aria-hidden', 'true');
  await no.press('Shift+Tab');
  await expect(yes).toBeFocused();
  await yes.press('Tab');
  await expect(no).toBeFocused();

  await page.keyboard.press('Escape');
  await expect(alert).toBeHidden();
  await expect(parent).toBeVisible();
  await expect(nestedLauncher).toBeFocused();
});
