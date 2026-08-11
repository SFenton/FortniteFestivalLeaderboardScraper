import { expect, test, type Page, type Route } from '@playwright/test';

const PLAYER = { type: 'player', accountId: 'web32-player', displayName: 'WEB32 Player' } as const;
const BAND = {
  type: 'band',
  bandId: 'web32-band',
  bandType: 'Band_Duets',
  teamKey: 'web32-player:web32-bandmate',
  displayName: 'WEB32 Duo',
  members: [
    { accountId: 'web32-player', displayName: 'WEB32 Player' },
    { accountId: 'web32-bandmate', displayName: 'WEB32 Bandmate' },
  ],
} as const;

test.beforeEach(async ({ page }) => {
  await installApiMocks(page);
});

test('desktop first-open chunks preserve loading, close/reopen, focus, and sorting', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'desktop interaction graph is covered once');
  const moduleRequests = trackModuleRequests(page);
  await seedState(page, null);
  await page.goto('/#/songs', { waitUntil: 'load' });
  await dismissOverlays(page);

  expect(moduleRequests.some(url => url.includes('/components/search/SearchModal.tsx'))).toBe(false);
  expect(moduleRequests.some(url => url.includes('@dnd-kit'))).toBe(false);

  await page.route('**/src/components/search/SearchModal.tsx*', async (route) => {
    await new Promise(resolve => setTimeout(resolve, 250));
    await route.continue();
  }, { times: 1 });

  const searchButton = page.getByTestId('desktop-header-search');
  await searchButton.click();
  await expect(page.getByTestId('search-modal-lazy-loading')).toBeVisible();
  const searchDialog = page.getByRole('dialog', { name: 'Search' });
  await expect(searchDialog).toBeVisible();
  expect(moduleRequests.some(url => url.includes('/components/search/SearchModal.tsx'))).toBe(true);

  await searchDialog.getByRole('button', { name: 'Close' }).click();
  await expect(searchDialog).toHaveCount(0, { timeout: 10_000 });

  const searchRequestCount = moduleRequests.filter(url => url.includes('/components/search/SearchModal.tsx')).length;
  await searchButton.click();
  await expect(searchDialog).toBeVisible();
  expect(moduleRequests.filter(url => url.includes('/components/search/SearchModal.tsx'))).toHaveLength(searchRequestCount);
  await searchDialog.getByRole('button', { name: 'Close' }).click();

  const sortButton = page.getByRole('button', { name: 'Sort' }).first();
  await sortButton.click();
  const sortDialog = page.getByRole('dialog', { name: 'Sort Songs' });
  await expect(sortDialog).toBeVisible();
  expect(moduleRequests.some(url => url.includes('/pages/songs/modals/SortModal.tsx'))).toBe(true);

  await sortDialog.getByRole('button', { name: 'Descending' }).click();
  await sortDialog.getByRole('button', { name: 'Apply' }).click();
  await expect(sortDialog).toHaveCount(0, { timeout: 10_000 });
  await expect(sortButton).toBeFocused();

  await sortButton.click();
  await expect(sortDialog).toBeVisible();
  await sortDialog.getByRole('button', { name: 'Close' }).click();
});

test('desktop profile selection and notifications load only on interaction', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'desktop shell interactions are covered once');
  const moduleRequests = trackModuleRequests(page);
  await seedState(page, null);
  await page.goto('/#/songs', { waitUntil: 'load' });
  await dismissOverlays(page);

  const profileButton = page.getByTestId('desktop-header-profile');
  await profileButton.click();
  const profileDialog = page.getByRole('dialog', { name: 'Search' });
  await expect(profileDialog).toBeVisible();
  await expect(profileDialog.getByRole('button', { name: 'Players' })).toBeVisible();
  await expect(profileDialog.getByRole('button', { name: 'Bands' })).toBeVisible();
  await expect(profileDialog.getByRole('button', { name: 'Songs' })).toHaveCount(0);
  await profileDialog.getByRole('button', { name: 'Close' }).click();
  await expect(profileDialog).toHaveCount(0, { timeout: 10_000 });
  expect(moduleRequests.some(url => url.includes('/components/search/SearchModal.tsx'))).toBe(true);

  await seedState(page, PLAYER);
  await page.reload({ waitUntil: 'load' });
  await dismissOverlays(page);
  expect(moduleRequests.some(url => url.includes('/components/notifications/MobileNotificationsModal.tsx'))).toBe(false);

  const notificationsButton = page.getByTestId('desktop-header-notifications');
  await expect(notificationsButton).toBeVisible();
  await notificationsButton.click();
  const notificationsDialog = page.getByRole('dialog', { name: 'Notifications' });
  await expect(notificationsDialog).toBeVisible();
  await expect(page.getByText('No notifications available')).toBeVisible();
  expect(moduleRequests.some(url => url.includes('/components/notifications/MobileNotificationsModal.tsx'))).toBe(true);

  await notificationsDialog.getByRole('button', { name: 'Close' }).click();
  await expect(notificationsButton).toBeFocused();
  await notificationsButton.click();
  await expect(notificationsDialog).toBeVisible();
});

test('desktop filters defer both Songs and selected-band controls', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'desktop filter interactions are covered once');
  const moduleRequests = trackModuleRequests(page);
  await seedState(page, PLAYER);
  await page.goto('/#/songs', { waitUntil: 'load' });
  await dismissOverlays(page);

  const filterButton = page.getByRole('button', { name: 'Filter' }).first();
  await filterButton.click();
  const filterDialog = page.getByRole('dialog', { name: 'Filter Songs' });
  await expect(filterDialog).toBeVisible();
  expect(moduleRequests.some(url => url.includes('/pages/songs/modals/FilterModal.tsx'))).toBe(true);
  await filterDialog.getByRole('button', { name: 'Close' }).click();
  await expect(filterButton).toBeFocused();

  await seedState(page, BAND);
  await page.reload({ waitUntil: 'load' });
  await dismissOverlays(page);
  expect(moduleRequests.some(url => url.includes('/pages/band/modals/BandInstrumentFilterModal.tsx'))).toBe(false);

  const bandFilterButton = page.getByTestId('band-filter-pill');
  await bandFilterButton.click();
  const bandFilterDialog = page.getByRole('dialog', { name: 'Filter Band Type' });
  await expect(bandFilterDialog).toBeVisible();
  await expect(bandFilterDialog.getByText('Instrument #1')).toBeVisible();
  expect(moduleRequests.some(url => url.includes('/pages/band/modals/BandInstrumentFilterModal.tsx'))).toBe(true);
  await bandFilterDialog.getByRole('button', { name: 'Close' }).click();
  await expect(bandFilterButton).toBeFocused();
});

test('mobile search keeps keyboard focus and restores the launch control', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mobile', 'mobile keyboard behavior is covered once');
  await seedState(page, null);
  await page.goto('/#/songs', { waitUntil: 'load' });
  await dismissOverlays(page);

  const searchButton = page.getByTestId('mobile-header-search');
  await searchButton.click();
  const dialog = page.getByRole('dialog', { name: 'Search' });
  const input = dialog.locator('input');
  await expect(dialog).toBeVisible();
  await expect(input).toBeFocused();

  await input.fill('WEB32');
  await input.press('Enter');
  await expect(input).not.toBeFocused();
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();
  await expect(searchButton).toBeFocused();
});

test('mobile Rivals search uses a zoom-safe input and restores Find Rival focus', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'mobile', 'mobile Rivals focus behavior is covered once');
  await seedState(page, PLAYER);
  await page.goto('/#/rivals', { waitUntil: 'load' });
  await dismissOverlays(page);

  const findRivalButton = page.getByRole('button', { name: 'Find Rival' });
  await page.evaluate(() => {
    const probeWindow = window as Window & { __rivalsSearchInitialFocusRect?: { top: number; bottom: number } };
    delete probeWindow.__rivalsSearchInitialFocusRect;
    const recordInitialFocus = (event: FocusEvent) => {
      if (!(event.target instanceof HTMLInputElement) || !event.target.closest('[role="dialog"]')) return;
      const rect = event.target.getBoundingClientRect();
      probeWindow.__rivalsSearchInitialFocusRect = { top: rect.top, bottom: rect.bottom };
      document.removeEventListener('focusin', recordInitialFocus, true);
    };
    document.addEventListener('focusin', recordInitialFocus, true);
  });
  await findRivalButton.click();

  const dialog = page.getByRole('dialog', { name: 'Search' });
  const input = dialog.getByRole('textbox', { name: 'Search players…' });
  await expect(dialog).toBeVisible();
  await expect(input).toBeFocused();
  expect(await input.evaluate(element => Number.parseFloat(getComputedStyle(element).fontSize))).toBeGreaterThanOrEqual(16);
  const initialFocusGeometry = await page.evaluate(() => {
    const probeWindow = window as Window & { __rivalsSearchInitialFocusRect?: { top: number; bottom: number } };
    return {
      rect: probeWindow.__rivalsSearchInitialFocusRect,
      viewportHeight: window.innerHeight,
    };
  });
  expect(initialFocusGeometry.rect?.top).toBeGreaterThanOrEqual(0);
  expect(initialFocusGeometry.rect?.bottom).toBeLessThanOrEqual(initialFocusGeometry.viewportHeight);

  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();
  await expect(findRivalButton).toBeFocused();
});

test('lazy chunk failure stays in an accessible fail-closed modal', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'chunk failure behavior is covered once');
  await seedState(page, null);
  await page.goto('/#/songs', { waitUntil: 'load' });
  await dismissOverlays(page);
  await page.route('**/src/components/search/SearchModal.tsx*', route => route.abort('failed'), { times: 1 });

  await page.getByTestId('desktop-header-search').click();
  const errorDialog = page.getByTestId('search-modal-lazy-error');
  await expect(errorDialog).toBeVisible();
  await expect(errorDialog.getByRole('alert')).toContainText('could not be loaded');
  await expect(errorDialog.getByRole('button', { name: 'Reload' })).toBeVisible();
  await errorDialog.getByRole('button', { name: 'Close' }).click();
  await expect(page.getByTestId('desktop-header-search')).toBeFocused();
});

function trackModuleRequests(page: Page) {
  const requests: string[] = [];
  page.on('request', request => {
    const url = request.url();
    if (url.includes('/src/') || url.includes('/node_modules/')) requests.push(url);
  });
  return requests;
}

async function seedState(page: Page, profile: typeof PLAYER | typeof BAND | null) {
  if (page.url() === 'about:blank') {
    await page.goto('/', { waitUntil: 'commit' });
  }
  await page.evaluate((profileValue) => {
    localStorage.clear();
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: true,
      showDrums: true,
      showVocals: true,
      showProLead: true,
      showProBass: true,
      showPeripheralVocals: true,
      showPeripheralCymbals: true,
      showPeripheralDrums: true,
    }));
    if (!profileValue) return;
    localStorage.setItem('fst:selectedProfile', JSON.stringify(profileValue));
    if (profileValue.type === 'player') {
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({
        accountId: profileValue.accountId,
        displayName: profileValue.displayName,
      }));
    }
  }, profile);
}

async function dismissOverlays(page: Page) {
  await page.waitForTimeout(750);
  let quietChecks = 0;
  for (let attempt = 0; attempt < 20; attempt += 1) {
    const firstRunClose = page.getByTestId('fre-close');
    if (await firstRunClose.isVisible().catch(() => false)) {
      await firstRunClose.click();
      await page.waitForTimeout(600);
      quietChecks = 0;
      continue;
    }
    const dismiss = page.getByRole('button', { name: 'Dismiss' });
    if (await dismiss.isVisible().catch(() => false)) {
      await dismiss.click();
      await page.waitForTimeout(500);
      quietChecks = 0;
      continue;
    }
    quietChecks += 1;
    if (quietChecks >= 3) return;
    await page.waitForTimeout(300);
  }
}

async function installApiMocks(page: Page) {
  await page.routeWebSocket('**/api/ws', () => {});
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (!path.startsWith('/api/')) return route.continue();
    if (path === '/api/service-info') {
      return json(route, {
        publishedScrapeId: 1236,
        activeScrapeId: null,
        publication: { publishedScrapeId: 1236, publicReadsFrozen: false },
        workerStatus: { status: 'offline', rawStatus: 'offline' },
      });
    }
    if (path === '/api/songs') {
      return json(route, {
        count: 1,
        currentSeason: 14,
        songs: [{ songId: 'web32-song', title: 'WEB32 Song', artist: 'WEB32 Artist', year: 2026 }],
      });
    }
    if (path === '/api/shop') return json(route, { count: 0, songs: [], newSongs: [], lastUpdated: null });
    if (path === '/api/version') return json(route, { version: 'web32' });
    if (path === '/api/account/name-refresh') {
      return json(route, { changed: 0, unchanged: 1, failed: 0, missing: 0, names: {}, changedAccountIds: [] });
    }
    if (path === `/api/player/${PLAYER.accountId}`) {
      return json(route, { accountId: PLAYER.accountId, displayName: PLAYER.displayName, totalScores: 0, scores: [] });
    }
    if (path === `/api/player/${PLAYER.accountId}/sync-status`) {
      return json(route, { accountId: PLAYER.accountId, isTracked: true, backfill: null, historyRecon: null });
    }
    if (path === `/api/player/${PLAYER.accountId}/notifications`) {
      return json(route, {
        generatedAt: '2026-07-25T00:00:00Z',
        sourceRunId: 1236,
        sourceCompletedAt: '2026-07-25T00:00:00Z',
        notificationsGenerated: true,
        items: [],
      });
    }
    if (path === `/api/player/${PLAYER.accountId}/bands`) {
      return json(route, { accountId: PLAYER.accountId, group: 'all', totalCount: 0, entries: [] });
    }
    if (path.startsWith(`/api/player/${PLAYER.accountId}/rivals/`)) {
      const combo = decodeURIComponent(path.slice(`/api/player/${PLAYER.accountId}/rivals/`.length));
      return json(route, { combo, above: [], below: [] });
    }
    if (path === `/api/bands/${BAND.bandId}`) {
      return json(route, {
        band: BAND,
        ranking: null,
        configurations: [{
          rawInstrumentCombo: '0:1',
          comboId: 'Solo_Guitar+Solo_Bass',
          instruments: ['Solo_Guitar', 'Solo_Bass'],
          assignmentKey: `${PLAYER.accountId}=Solo_Guitar|web32-bandmate=Solo_Bass`,
          appearanceCount: 1,
          memberInstruments: {
            [PLAYER.accountId]: 'Solo_Guitar',
            'web32-bandmate': 'Solo_Bass',
          },
        }],
      });
    }
    if (path === `/api/bands/${BAND.bandId}/notifications`) {
      return json(route, {
        generatedAt: '2026-07-25T00:00:00Z',
        sourceRunId: 1236,
        sourceCompletedAt: '2026-07-25T00:00:00Z',
        notificationsGenerated: true,
        items: [],
      });
    }
    return json(route, {});
  });
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}
