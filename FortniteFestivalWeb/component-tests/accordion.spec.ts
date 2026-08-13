import { expect, test } from '@playwright/test';

test('collapsed disclosure stays out of tab order and restores focus when closed', async ({ mount }) => {
  const component = await mount('components/common/Accordion/FocusLifecycle');
  const trigger = component.getByRole('button', { name: /Advanced filters/ });
  const panel = component.locator('[role="region"]');
  const panelAction = component.getByRole('button', { name: 'Panel action' });
  const after = component.getByRole('button', { name: 'After accordion' });

  await expect(trigger).toHaveAttribute('aria-expanded', 'false');
  await expect(panel).toHaveAttribute('inert', '');
  await expect(panel).toHaveAttribute('aria-hidden', 'true');
  await trigger.focus();
  await trigger.press('Tab');
  await expect(after).toBeFocused();

  await trigger.press('Enter');
  await expect(trigger).toHaveAttribute('aria-expanded', 'true');
  await expect(panel).not.toHaveAttribute('inert');
  await expect(panel).not.toHaveAttribute('aria-hidden');
  await expect(component.getByRole('region', { name: /Advanced filters/ })).toBeVisible();
  await trigger.press('Tab');
  await expect(panelAction).toBeFocused();

  await trigger.evaluate(element => (element as HTMLButtonElement).click());
  await expect(trigger).toBeFocused();
  await expect(panel).toHaveAttribute('inert', '');
});
