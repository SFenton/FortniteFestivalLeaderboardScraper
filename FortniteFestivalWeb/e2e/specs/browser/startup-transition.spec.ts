import type { Locator, Page } from '@playwright/test';
import { test, expect } from '../../fixtures/test';
import { createPopulatedScenario } from '../../fixtures/scenarios';

test.use({ scenario: createPopulatedScenario() });

test.beforeEach(async ({ appState }) => {
  await appState.reset();
});

async function expectCentered(splash: Locator, page: Page) {
  const spinner = splash.getByTestId('arc-spinner');
  const box = await spinner.boundingBox();
  const viewport = page.viewportSize();
  expect(box).not.toBeNull();
  expect(viewport).not.toBeNull();
  await expect(spinner).toHaveCSS('width', '48px');
  await expect(spinner).toHaveCSS('height', '48px');
  expect(Math.abs(box!.x + box!.width / 2 - viewport!.width / 2)).toBeLessThanOrEqual(1);
  expect(Math.abs(box!.y + box!.height / 2 - viewport!.height / 2)).toBeLessThanOrEqual(1);
}

async function visibleSpinnerCount(page: Page): Promise<number> {
  return page.getByTestId('arc-spinner').evaluateAll(spinners => spinners.filter(spinner => {
    const bounds = spinner.getBoundingClientRect();
    if (bounds.width === 0 || bounds.height === 0) return false;

    let opacity = 1;
    let current: Element | null = spinner;
    while (current) {
      const style = getComputedStyle(current);
      if (style.display === 'none' || style.visibility === 'hidden') return false;
      opacity *= Number(style.opacity);
      current = current.parentElement;
    }
    return opacity > 0.01;
  }).length);
}

function createReleaseGate() {
  let release!: () => void;
  const promise = new Promise<void>(resolve => {
    release = resolve;
  });
  return { promise, release };
}

test('keeps one centered purple spinner through availability and app readiness, then reveals the shell underneath', async ({
  page,
  api,
}) => {
  const scenario = api.current();
  const availabilityGate = createReleaseGate();
  const songsGate = createReleaseGate();
  await page.route('**/api/service-info*', async route => {
    await availabilityGate.promise;
    await route.fulfill({ json: scenario.serviceInfo });
  });
  await page.route(/\/api\/songs(?:\?.*)?$/, async route => {
    await songsGate.promise;
    await route.fulfill({ json: scenario.songs });
  });

  await page.goto('/#/songs', { waitUntil: 'domcontentloaded' });

  const splash = page.getByTestId('startup-splash');
  await expect(splash).toBeVisible();
  await expect(splash).toHaveAttribute('aria-hidden', 'true');
  await expect(splash).not.toHaveAttribute('role', 'status');
  await expect(splash).toHaveCSS('background-color', 'rgb(26, 8, 48)');
  await expect(splash.locator('img')).toHaveCount(0);
  await expect(page.getByText('Loading published data...')).toHaveCount(0);
  await expect(page.getByText('Checking Festival Score Tracker status...')).toHaveCount(0);
  await expectCentered(splash, page);
  expect(await visibleSpinnerCount(page)).toBe(1);

  availabilityGate.release();
  const shell = page.getByTestId('app-shell');
  await expect(shell).toHaveAttribute('data-startup-phase', 'waiting');
  await expect(shell).toHaveAttribute('inert', '');
  await expect(shell).toHaveAttribute('aria-hidden', 'true');
  await expect(shell).toHaveCSS('opacity', '0');
  await expectCentered(splash, page);
  expect(await visibleSpinnerCount(page)).toBe(1);

  songsGate.release();
  await expect(splash).toHaveAttribute('data-phase', 'revealing', { timeout: 5_000 });
  await expect(splash).toHaveCSS('pointer-events', 'auto');
  await expect(shell).toHaveCSS('opacity', '1');
  await expect(shell).toHaveAttribute('inert', '');
  await expect(shell).toHaveAttribute('aria-hidden', 'true');

  await expect(splash).toHaveCount(0);
  await expect(shell).not.toHaveAttribute('inert');
  await expect(shell).not.toHaveAttribute('aria-hidden');
  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();
});

test('holds body portals until entry and gives First Run priority over the changelog', async ({
  page,
  appState,
  api,
}) => {
  await appState.clearFirstRun();
  await page.localStorage.removeItem('fst:changelog');
  api.override({
    path: '/api/songs',
    status: 200,
    body: api.current().songs,
    delayMs: 700,
    remaining: 1,
  });

  await page.goto('/#/songs', { waitUntil: 'domcontentloaded' });
  await expect.poll(() => api.count('/api/songs')).toBe(1);

  const splash = page.getByTestId('startup-splash');
  await expect(splash).toBeVisible();
  await expect(page.getByTestId('fre-overlay')).toHaveCount(0);
  await expect(page.getByRole('dialog', { name: /What's New/i })).toHaveCount(0);

  await expect(splash).toHaveCount(0, { timeout: 5_000 });
  await expect(page.getByTestId('fre-overlay')).toBeVisible();
  await expect(page.getByRole('dialog', { name: /What's New/i })).toHaveCount(0);
});

test('admits an eligible changelog only after the startup splash exits', async ({
  page,
}) => {
  await page.localStorage.removeItem('fst:changelog');
  await page.goto('/#/settings', { waitUntil: 'domcontentloaded' });

  const splash = page.getByTestId('startup-splash');
  await expect(splash).toBeVisible();
  await expect(page.getByRole('dialog', { name: /What's New/i })).toHaveCount(0);

  await expect(splash).toHaveCount(0);
  await expect(page.getByRole('dialog', { name: /What's New/i })).toBeVisible();
});

test('honors reduced motion and never replays startup on later route changes', async ({
  page,
  api,
}) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const availabilityGate = createReleaseGate();
  await page.route('**/api/service-info*', async route => {
    await availabilityGate.promise;
    await route.fulfill({ json: api.current().serviceInfo });
  });
  await page.goto('/#/settings', { waitUntil: 'domcontentloaded' });

  const splash = page.getByTestId('startup-splash');
  await expect(splash).toBeVisible();
  await expect(splash).toHaveCSS('transition-property', 'none');
  await expect(splash.getByTestId('arc-spinner')).toHaveCSS('animation-name', 'none');

  availabilityGate.release();
  const shell = page.getByTestId('app-shell');
  await expect(splash).toHaveCount(0);
  await expect(shell).toHaveAttribute('data-startup-phase', 'entered');

  await page.evaluate(() => {
    const state = window as Window & { __startupSplashAdds?: number };
    state.__startupSplashAdds = 0;
    const observer = new MutationObserver(records => {
      for (const record of records) {
        for (const node of record.addedNodes) {
          if (
            node instanceof Element
            && (
              node.matches('[data-testid="startup-splash"]')
              || node.querySelector('[data-testid="startup-splash"]')
            )
          ) {
            state.__startupSplashAdds = (state.__startupSplashAdds ?? 0) + 1;
          }
        }
      }
    });
    observer.observe(document.body, { childList: true, subtree: true });
  });

  await page.getByRole('link', { name: 'Licenses', exact: true }).click();
  await expect(page).toHaveURL(/#\/settings\/licenses$/);
  await expect(page.getByRole('heading', { name: 'Licenses' })).toBeVisible();
  expect(await page.evaluate(() => (
    (window as Window & { __startupSplashAdds?: number }).__startupSplashAdds ?? 0
  ))).toBe(0);
});

test('wildcard redirects release startup instead of deadlocking', async ({
  page,
}) => {
  await page.goto('/#/this-route-does-not-exist', { waitUntil: 'domcontentloaded' });

  await expect(page.getByTestId('startup-splash')).toHaveCount(0);
  await expect(page).toHaveURL(/#\/songs$/);
  await expect(page.getByText('Deterministic Song 2', { exact: true })).toBeVisible();
});
