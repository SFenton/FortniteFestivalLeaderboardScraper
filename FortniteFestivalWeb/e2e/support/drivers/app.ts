import { expect, type Page } from '@playwright/test';

export async function gotoAppRoute(page: Page, route: string): Promise<void> {
  const normalized = route.startsWith('/') ? route : `/${route}`;
  await page.goto(`/#${normalized}`, { waitUntil: 'load' });
  await dismissObstructions(page);
}

export async function dismissObstructions(page: Page): Promise<void> {
  for (let attempt = 0; attempt < 4; attempt += 1) {
    const firstRunClose = page.getByTestId('fre-close');
    if (await firstRunClose.isVisible().catch(() => false)) {
      await firstRunClose.click({ force: true });
      await page.getByTestId('fre-overlay').waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
      continue;
    }

    const dismiss = page.getByRole('button', { name: 'Dismiss', exact: true });
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.click({ force: true });
      await dismiss.waitFor({ state: 'hidden', timeout: 2_000 }).catch(() => {});
      continue;
    }

    break;
  }
}

export async function expectMainContent(page: Page, text: string | RegExp): Promise<void> {
  await expect(page.locator('#main-content')).toContainText(text, { timeout: 15_000 });
}
