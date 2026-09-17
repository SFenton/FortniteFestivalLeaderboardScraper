import { errors, expect, type Locator, type Page } from '@playwright/test';

const OVERLAY_ACTION_TIMEOUT_MS = 1_000;
const OVERLAY_SETTLE_TIMEOUT_MS = 2_000;
const OVERLAY_ATTEMPTS = 12;
const OVERLAY_POLL_INTERVAL_MS = 150;
const OVERLAY_QUIET_CHECKS = 8;

export async function gotoAppRoute(page: Page, route: string): Promise<void> {
  const normalized = route.startsWith('/') ? route : `/${route}`;
  await page.goto(`/#${normalized}`, { waitUntil: 'load' });
  await dismissObstructions(page);
}

export async function dismissObstructions(page: Page): Promise<void> {
  let quietChecks = 0;
  for (let attempt = 0; attempt < OVERLAY_ATTEMPTS; attempt += 1) {
    const firstRunClose = page.getByTestId('fre-close').last();
    if (await dismissControl(firstRunClose)) {
      quietChecks = 0;
      continue;
    }

    const dismiss = page.getByRole('button', { name: 'Dismiss', exact: true }).last();
    if (await dismissControl(dismiss)) {
      quietChecks = 0;
      continue;
    }

    quietChecks += 1;
    if (quietChecks >= OVERLAY_QUIET_CHECKS) return;
    await page.waitForTimeout(OVERLAY_POLL_INTERVAL_MS);
  }
}

async function dismissControl(locator: Locator): Promise<boolean> {
  if (!(await locator.isVisible().catch(() => false))) return false;

  try {
    await locator.click({ force: true, timeout: OVERLAY_ACTION_TIMEOUT_MS });
  } catch (error) {
    if (!(error instanceof errors.TimeoutError)) throw error;
    return true;
  }

  try {
    await locator.waitFor({ state: 'hidden', timeout: OVERLAY_SETTLE_TIMEOUT_MS });
  } catch (error) {
    if (!(error instanceof errors.TimeoutError)) throw error;
  }
  return true;
}

export async function expectMainContent(page: Page, text: string | RegExp): Promise<void> {
  await expect(page.locator('#main-content')).toContainText(text, { timeout: 15_000 });
}
