import { test, expect } from '../../fixtures/test';
import { E2E_BAND, E2E_PLAYER, E2E_RIVAL, E2E_SONG_ID, createPopulatedScenario } from '../../fixtures/scenarios';
import { expectMainContent, gotoAppRoute } from '../../support/drivers/app';
import { isMobileProject } from '../../support/projects';

test.use({ scenario: createPopulatedScenario() });

test.describe('public route contracts', () => {
  test.beforeEach(async ({ appState }) => {
    await appState.reset();
  });

  test('root redirects to Songs with replace semantics', async ({ page }) => {
    await page.goto('/#/', { waitUntil: 'load' });
    await expect(page).toHaveURL(/#\/songs$/);
    await expectMainContent(page, 'Deterministic Song');
  });

  const routes = [
    { path: '/songs', content: 'Deterministic Song' },
    { path: `/songs/${E2E_SONG_ID}`, content: 'Score Player' },
    { path: `/songs/${E2E_SONG_ID}/Solo_Guitar`, content: 'Score Player' },
    { path: `/songs/${E2E_SONG_ID}/bands/Band_Duets`, content: 'Band Member' },
    { path: '/shop', content: 'A Very Long Deterministic Festival Song Title' },
    { path: '/manual', content: 'Navigation Basics' },
    { path: '/leaderboards', content: 'Ranked Player' },
    { path: '/leaderboards/all', content: 'Ranked Player' },
    { path: '/leaderboards/bands/Band_Duets', content: 'Band 1 A' },
    { path: `/bands/player/${E2E_PLAYER.accountId}`, content: 'E2E Player A' },
    { path: `/bands/${E2E_BAND.bandId}`, content: 'E2E Player A' },
    { path: `/bands?bandType=${E2E_BAND.bandType}&teamKey=${encodeURIComponent(E2E_BAND.teamKey)}&names=${encodeURIComponent(E2E_BAND.displayName)}`, content: 'E2E Player A' },
    { path: '/settings', content: 'App Settings' },
    { path: '/settings/licenses', content: '@playwright/test' },
  ] as const;

  for (const route of routes) {
    test(`${route.path} renders meaningful content`, async ({ page }) => {
      await gotoAppRoute(page, route.path);
      await expectMainContent(page, route.content);
    });
  }

  test('unknown routes retain the URL and render an intentional not-found page', async ({ page }) => {
    await gotoAppRoute(page, '/missing/deep-link');

    await expect(page).toHaveURL(/#\/missing\/deep-link$/);
    await expect(page).toHaveTitle(/Not Found/);
    await expect(page.getByRole('heading', { level: 1, name: 'Not Found' })).toBeVisible();
    await expect(page.getByRole('link', { name: 'Go to Songs' })).toHaveAttribute('href', '#/songs');
  });

  test('malformed encoded song links do not crash the application shell', async ({ page }) => {
    const pageErrors: string[] = [];
    page.on('pageerror', error => pageErrors.push(error.message));

    await gotoAppRoute(page, '/songs/%E0%A4%A');

    await expect(page.locator('main#main-content')).toBeVisible();
    await expect(page).toHaveTitle(/Song/);
    expect(pageErrors).toEqual([]);
  });

  test('trailing slashes retain the matched route title and content', async ({ page }, testInfo) => {
    await gotoAppRoute(page, '/settings/');

    await expect(page).toHaveURL(/#\/settings\/$/);
    await expect(page).toHaveTitle(/Settings/);
    await expect(page.getByRole('heading', { level: 1, name: 'Settings' })).toBeVisible();
    await expect(page.getByRole('heading', { level: 1, name: 'Not Found' })).toHaveCount(0);
    if (isMobileProject(testInfo.project.name)) {
      await expect(page.getByTestId('bottom-nav-settings')).toHaveAttribute('aria-current', 'page');
    }
  });

  test('Licenses retains Settings tab ownership on mobile', async ({ page }, testInfo) => {
    test.skip(!isMobileProject(testInfo.project.name), 'mobile tab ownership');
    await gotoAppRoute(page, '/settings/licenses');

    await expect(page.getByTestId('bottom-nav-settings')).toHaveAttribute('aria-current', 'page');
  });
});

test.describe('selected player route contracts', () => {
  test.beforeEach(async ({ appState }) => {
    await appState.reset();
    await appState.selectPlayer();
  });

  const routes = [
    { path: `/player/${E2E_PLAYER.accountId}`, content: E2E_PLAYER.displayName },
    { path: '/statistics', content: E2E_PLAYER.displayName },
    { path: '/suggestions', content: 'Deterministic Song' },
    { path: '/rivals', content: E2E_RIVAL.displayName },
    { path: '/rivals/all', content: E2E_RIVAL.displayName },
    { path: `/rivals/${E2E_RIVAL.accountId}`, content: E2E_RIVAL.displayName },
    { path: `/rivals/${E2E_RIVAL.accountId}/rivalry`, content: 'Deterministic Song' },
    { path: '/compete', content: 'Leaderboards' },
    { path: `/songs/${E2E_SONG_ID}/Solo_Guitar/history`, content: 'Dec 25, 2025' },
  ] as const;

  for (const route of routes) {
    test(`${route.path} renders player content`, async ({ page }) => {
      await gotoAppRoute(page, route.path);
      await expectMainContent(page, route.content);
    });
  }
});

test.describe('selected band route contracts', () => {
  test.beforeEach(async ({ appState }) => {
    await appState.reset();
    await appState.selectBand();
  });

  test('/statistics renders selected band statistics', async ({ page }) => {
    await gotoAppRoute(page, '/statistics');
    await expectMainContent(page, 'E2E Player A');
  });

  test('/suggestions renders selected band suggestions', async ({ page }) => {
    await gotoAppRoute(page, '/suggestions');
    await expectMainContent(page, 'Deterministic Song');
  });

  for (const path of [
    '/rivals',
    '/rivals/all',
    `/rivals/${E2E_RIVAL.accountId}`,
    `/rivals/${E2E_RIVAL.accountId}/rivalry`,
    '/compete',
  ]) {
    test(`${path} redirects a selected band to Songs`, async ({ page }) => {
      await page.goto(`/#${path}`, { waitUntil: 'load' });
      await expect(page).toHaveURL(/#\/songs$/, { timeout: 15_000 });
      await expectMainContent(page, 'Deterministic Song');
    });
  }
});

test.describe('guarded route redirects', () => {
  test.beforeEach(async ({ appState }) => {
    await appState.reset();
  });

  for (const path of [
    '/rivals',
    '/rivals/all',
    `/rivals/${E2E_RIVAL.accountId}`,
    `/rivals/${E2E_RIVAL.accountId}/rivalry`,
    '/suggestions',
    '/compete',
    '/statistics',
  ]) {
    test(`${path} redirects to Songs without a selected profile`, async ({ page }) => {
      await page.goto(`/#${path}`, { waitUntil: 'load' });
      await expect(page).toHaveURL(/#\/songs$/, { timeout: 15_000 });
      await expectMainContent(page, 'Deterministic Song');
    });
  }

  test('guard redirects replace only the denied route history entry', async ({ page }) => {
    await gotoAppRoute(page, '/settings');
    await expectMainContent(page, 'App Settings');

    await page.evaluate(() => {
      window.location.hash = '#/rivals';
    });
    await expect(page).toHaveURL(/#\/songs$/, { timeout: 15_000 });

    await page.goBack();
    await expect(page).toHaveURL(/#\/settings$/);
    await expectMainContent(page, 'App Settings');
  });
});
