import { test, expect } from '../../fixtures/test';
import { E2E_BAND, E2E_PLAYER, E2E_RIVAL, E2E_SONG_ID, createPopulatedScenario } from '../../fixtures/scenarios';
import { expectMainContent, gotoAppRoute } from '../../support/drivers/app';

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
});
