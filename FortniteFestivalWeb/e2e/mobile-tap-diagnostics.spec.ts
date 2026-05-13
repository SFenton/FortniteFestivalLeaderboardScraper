import type { Locator, Page, TestInfo } from '@playwright/test';
import { test, expect } from './fixtures/fre';
import { changelogHash } from '../src/changelog';
import { getTapDiagnosticsDump, gotoWithTapDiagnostics, tapAndExpect } from './fixtures/tapDiagnostics';

const TEST_PLAYER = { accountId: '195e93ef108143b2975ee46662d4d0e1', displayName: 'SFentonX' };

test.describe('Mobile tap diagnostics', () => {
  test.beforeEach(async ({ page, freState }, testInfo) => {
    test.skip(!testInfo.project.name.startsWith('mobile'), 'mobile-only tap diagnostics');
    await installApiMocks(page);
    await freState.resetAppState();
    await freState.setSettings({ hideItemShop: true, disableShopHighlighting: true });
    await page.evaluate(
      (hash) => localStorage.setItem('fst:changelog', JSON.stringify({ version: 'e2e', hash })),
      changelogHash(),
    );
  });

  test('rapid mobile shell actions report non-responsive taps with hit-test context', async ({ page, fre }, testInfo) => {
    await gotoWithTapDiagnostics(page, '/songs');
    await fre.waitForVisible();
    await dismissFre(page, fre, testInfo, 'dismiss initial songs FRE');

    const searchDialog = page.getByRole('dialog', { name: 'Search' });
    await tapAndExpect(
      page,
      testInfo,
      'open header search modal',
      page.getByTestId('mobile-header-search'),
      () => searchDialog.isVisible().catch(() => false),
      { retryOnFailure: true },
    );
    await tapAndExpect(
      page,
      testInfo,
      'close header search modal',
      searchDialog.getByRole('button', { name: 'Close' }),
      async () => !(await searchDialog.isVisible().catch(() => false)),
      { retryOnFailure: true },
    );

    await tapAndExpect(
      page,
      testInfo,
      'open header profile search',
      page.getByTestId('mobile-header-profile'),
      () => searchDialog.isVisible().catch(() => false),
      { retryOnFailure: true },
    );

    await searchDialog.getByRole('textbox').fill('sfen');
    const playerResult = page.getByTestId('search-player-result').filter({ hasText: TEST_PLAYER.displayName }).first();
    await expect(playerResult).toBeVisible({ timeout: 5_000 });
    await tapAndExpect(
      page,
      testInfo,
      'navigate to searched player',
      playerResult,
      () => page.evaluate((accountId) => window.location.hash.includes(`/player/${accountId}`), TEST_PLAYER.accountId),
      { retryOnFailure: true },
    );

    await dismissFreIfVisible(page, fre, testInfo, 'dismiss player statistics FRE');

    const selectProfile = page.getByTestId('select-profile-pill');
    await expect(selectProfile).toBeVisible({ timeout: 10_000 });
    await tapAndExpect(
      page,
      testInfo,
      'select searched player as profile',
      selectProfile,
      () => page.evaluate(() => window.location.hash.includes('/statistics')),
      { retryOnFailure: true, timeout: 2_500 },
    );

    await rapidTab(page, testInfo, 'songs');
    await rapidTab(page, testInfo, 'songs', '/songs');
    await dismissFreIfVisible(page, fre, testInfo, 'dismiss player-gated songs FRE');
    await rapidTab(page, testInfo, 'statistics', '/statistics');
    await rapidTab(page, testInfo, 'compete', '/compete');
    await dismissFreIfVisible(page, fre, testInfo, 'dismiss compete FRE');
    await rapidTab(page, testInfo, 'settings', '/settings');
    await dismissFreIfVisible(page, fre, testInfo, 'dismiss settings FRE');
    await rapidTab(page, testInfo, 'songs', '/songs');

    const dump = await getTapDiagnosticsDump(page, 120);
    await testInfo.attach('mobile-tap-diagnostics-success.json', {
      body: JSON.stringify(dump, null, 2),
      contentType: 'application/json',
    });
    expect(dump?.records.some(record => record.kind === 'event' && record.eventType === 'click')).toBe(true);
  });

  test('post-navigation overlays do not steal immediate mobile taps', async ({ page, fre }, testInfo) => {
    await gotoWithTapDiagnostics(page, '/songs');
    await fre.waitForVisible();
    await dismissFre(page, fre, testInfo, 'dismiss songs FRE for no-FRE control');

    await gotoWithTapDiagnostics(page, '/settings');
    await dismissFreIfVisible(page, fre, testInfo, 'dismiss settings FRE for no-FRE control');
    await page.getByTestId('bottom-nav-songs').click();
    await expectHitTargetAvailable(page, testInfo, 'bottom nav no-FRE leaves header search tappable', '[data-testid="mobile-header-search"]');

    await gotoWithTapDiagnostics(page, '/settings');
    await dismissFreIfVisible(page, fre, testInfo, 'dismiss settings FRE before sidebar control');
    await page.getByRole('button', { name: 'Open navigation' }).click();
    await expect(page.getByRole('link', { name: /^Songs$/ })).toBeVisible({ timeout: 5_000 });
    await page.getByRole('link', { name: /^Songs$/ }).click();
    await expectHitTargetAvailable(page, testInfo, 'sidebar no-FRE leaves header search tappable immediately', '[data-testid="mobile-header-search"]');

    await page.evaluate(() => localStorage.removeItem('fst:firstRun'));
    await gotoWithTapDiagnostics(page, '/settings');
    await dismissFreIfVisible(page, fre, testInfo, 'dismiss settings FRE before songs FRE entry check');
    await page.getByTestId('bottom-nav-songs').click();
    await expectHitTargetAvailable(page, testInfo, 'entering songs FRE is not an invisible hit-test blocker', '[data-testid="mobile-header-search"]');
    await dismissFreIfVisible(page, fre, testInfo, 'dismiss songs FRE after entry check');

    const searchDialog = page.getByRole('dialog', { name: 'Search' });
    await page.getByTestId('mobile-header-search').click();
    await expect(searchDialog).toBeVisible({ timeout: 5_000 });
    await searchDialog.getByRole('button', { name: 'Close' }).click();
    await expectHitTargetAvailable(page, testInfo, 'closed search modal leaves bottom nav tappable immediately', '[data-testid="bottom-nav-settings"]');
  });
});

async function expectHitTargetAvailable(page: Page, testInfo: TestInfo, label: string, targetSelector: string) {
  const result = await page.evaluate((selector) => {
    const target = document.querySelector(selector);
    const rect = target?.getBoundingClientRect();
    const point = rect
      ? { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 }
      : null;
    const top = point ? document.elementFromPoint(point.x, point.y) : null;
    const stack = point ? document.elementsFromPoint(point.x, point.y) : [];
    const describe = (element: Element | null) => {
      if (!element) return null;
      const htmlElement = element instanceof HTMLElement ? element : null;
      const style = htmlElement ? getComputedStyle(htmlElement) : null;
      const bounds = htmlElement?.getBoundingClientRect();
      return {
        tag: element.tagName.toLowerCase(),
        testId: element.getAttribute('data-testid') || undefined,
        aria: element.getAttribute('aria-label') || undefined,
        text: htmlElement?.innerText?.replace(/\s+/g, ' ').trim().slice(0, 80) || undefined,
        pointerEvents: style?.pointerEvents,
        opacity: style?.opacity,
        position: style?.position,
        zIndex: style?.zIndex,
        rect: bounds ? { x: Math.round(bounds.x), y: Math.round(bounds.y), w: Math.round(bounds.width), h: Math.round(bounds.height) } : undefined,
      };
    };
    return {
      selector,
      url: window.location.href,
      state: window.__fstTapDiagnostics?.dump(1).state,
      target: describe(target),
      top: describe(top),
      targetOwnsTop: Boolean(target && top && (target === top || target.contains(top))),
      stack: stack.slice(0, 8).map(describe),
    };
  }, targetSelector);

  if (!result.targetOwnsTop) {
    await testInfo.attach(`${label.toLowerCase().replace(/[^a-z0-9]+/g, '-')}-hit-test.json`, {
      body: JSON.stringify(result, null, 2),
      contentType: 'application/json',
    });
  }
  expect(result.targetOwnsTop, `${label}: top hit-test element should be the requested target or its child`).toBe(true);
}

async function rapidTab(page: Page, testInfo: TestInfo, tabKey: string, expectedRoute?: string) {
  try {
    await expect(page.getByTestId(`bottom-nav-${tabKey}`)).toBeVisible({ timeout: 10_000 });
  } catch (error) {
    await attachShellSnapshot(page, testInfo, `missing bottom nav ${tabKey}`);
    throw error;
  }
  await tapAndExpect(
    page,
    testInfo,
    expectedRoute ? `switch bottom tab to ${tabKey} route ${expectedRoute}` : `switch bottom tab to ${tabKey}`,
    page.getByTestId(`bottom-nav-${tabKey}`),
    () => page.evaluate(
      ({ expectedRoute: route, expectedTab }) => {
        const activeTab = window.__fstTapDiagnostics?.dump(1).state.activeTab;
        return activeTab === expectedTab && (!route || window.location.hash.includes(route));
      },
      { expectedRoute, expectedTab: tabKey },
    ),
    { retryOnFailure: true, timeout: 2_500 },
  );
}

async function attachShellSnapshot(page: Page, testInfo: TestInfo, label: string) {
  const snapshot = await page.evaluate(() => {
    const describeElement = (element: Element | null) => {
      if (!element) return null;
      const htmlElement = element instanceof HTMLElement ? element : null;
      const style = htmlElement ? getComputedStyle(htmlElement) : null;
      const bounds = htmlElement?.getBoundingClientRect();
      return {
        tag: element.tagName.toLowerCase(),
        testId: element.getAttribute('data-testid') || undefined,
        aria: element.getAttribute('aria-label') || undefined,
        text: htmlElement?.innerText?.replace(/\s+/g, ' ').trim().slice(0, 160) || undefined,
        pointerEvents: style?.pointerEvents,
        opacity: style?.opacity,
        position: style?.position,
        zIndex: style?.zIndex,
        rect: bounds ? { x: Math.round(bounds.x), y: Math.round(bounds.y), w: Math.round(bounds.width), h: Math.round(bounds.height) } : undefined,
      };
    };
    return {
      href: window.location.href,
      innerWidth: window.innerWidth,
      innerHeight: window.innerHeight,
      state: window.__fstTapDiagnostics?.dump(1).state,
      bottomNavTestIds: Array.from(document.querySelectorAll('[data-testid^="bottom-nav-"]')).map(element => element.getAttribute('data-testid')),
      mobileHeaderSearch: Boolean(document.querySelector('[data-testid="mobile-header-search"]')),
      freOverlay: describeElement(document.querySelector('[data-testid="fre-overlay"]')),
      searchDialog: describeElement(document.querySelector('[role="dialog"][aria-label="Search"]')),
      bodyText: document.body.innerText.replace(/\s+/g, ' ').trim().slice(0, 1_000),
    };
  });
  await testInfo.attach(`${label.toLowerCase().replace(/[^a-z0-9]+/g, '-')}-shell-snapshot.json`, {
    body: JSON.stringify(snapshot, null, 2),
    contentType: 'application/json',
  });
}

async function dismissFreIfVisible(page: Page, fre: { closeButton: Locator; isVisible: () => Promise<boolean> }, testInfo: TestInfo, label: string) {
  try {
    await page.locator('[data-testid="fre-card"]').waitFor({ state: 'visible', timeout: 2_500 });
  } catch {
    return;
  }
  if (await fre.isVisible()) await dismissFre(page, fre, testInfo, label);
}

async function dismissFre(page: Page, fre: { closeButton: Locator; isVisible: () => Promise<boolean> }, testInfo: TestInfo, label: string) {
  await tapAndExpect(
    page,
    testInfo,
    label,
    fre.closeButton,
    async () => !(await fre.isVisible()),
    { retryOnFailure: true, timeout: 1_600 },
  );
}

async function installApiMocks(page: Page) {
  await page.route(/^https?:\/\/[^/]+\/api\//, async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;

    if (path === '/api/songs') {
      await route.fulfill({ json: songsResponse() });
      return;
    }

    if (path === '/api/account/search') {
      await route.fulfill({ json: { results: [TEST_PLAYER] } });
      return;
    }

    if (path === '/api/bands/search') {
      await route.fulfill({ json: { query: url.searchParams.get('q') ?? '', normalizedQuery: url.searchParams.get('q') ?? '', rankBy: 'adjusted', page: 1, pageSize: 10, totalCount: 0, isAmbiguous: false, needsDisambiguation: false, interpretations: [], results: [] } });
      return;
    }

    if (path === `/api/player/${TEST_PLAYER.accountId}`) {
      await route.fulfill({ json: { accountId: TEST_PLAYER.accountId, displayName: TEST_PLAYER.displayName, totalScores: 0, scores: [] } });
      return;
    }

    if (path === `/api/player/${TEST_PLAYER.accountId}/stats`) {
      await route.fulfill({ json: { accountId: TEST_PLAYER.accountId, totalSongs: 0, instruments: [], compositeRanks: null, instrumentRanks: [], bands: null } });
      return;
    }

    if (path === `/api/player/${TEST_PLAYER.accountId}/sync-status`) {
      await route.fulfill({ json: syncStatusResponse() });
      return;
    }

    if (path === `/api/player/${TEST_PLAYER.accountId}/notifications` || /^\/api\/bands\/[^/]+\/notifications$/.test(path)) {
      await route.fulfill({ json: emptyNotificationsResponse() });
      return;
    }

    if (/^\/api\/rankings\/[^/]+\/[a-f0-9]+$/.test(path)) {
      await route.fulfill({ json: rankingResponse(path.split('/')[3] ?? 'Solo_Guitar') });
      return;
    }

    if (path.includes('/history')) {
      await route.fulfill({ json: { accountId: TEST_PLAYER.accountId, instrument: 'Solo_Guitar', rankBy: 'totalscore', days: 30, history: [] } });
      return;
    }

    if (path === '/api/shop') {
      await route.fulfill({ json: { songs: [], lastUpdated: new Date(0).toISOString() } });
      return;
    }

    if (path === '/api/service-info') {
      await route.fulfill({ json: { lastCompletedUpdate: null, currentUpdate: { status: 'idle', startedAt: null, phase: null, subOperation: null }, nextScheduledUpdateAt: null } });
      return;
    }

    await route.fulfill({ json: {} });
  });
}

function songsResponse() {
  return {
    count: 1,
    currentSeason: 9,
    songs: [{
      songId: 'song-a',
      title: 'Tap Diagnostic Song',
      artist: 'Festival QA',
      year: 2026,
      durationSeconds: 180,
      difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 },
      maxScores: { Solo_Guitar: 100000, Solo_Bass: 90000, Solo_Drums: 110000, Solo_Vocals: 80000 },
    }],
  };
}

function syncStatusResponse() {
  return {
    accountId: TEST_PLAYER.accountId,
    isTracked: false,
    pendingRankUpdate: false,
    backfill: null,
    historyRecon: null,
    rivals: null,
    postScrape: null,
  };
}

function emptyNotificationsResponse() {
  return {
    generatedAt: new Date(0).toISOString(),
    expiresAfterHours: 24,
    sourceRunId: null,
    sourceCompletedAt: null,
    notificationsGenerated: false,
    items: [],
  };
}

function rankingResponse(instrument: string) {
  return {
    accountId: TEST_PLAYER.accountId,
    displayName: TEST_PLAYER.displayName,
    instrument,
    totalRankedAccounts: 1,
    songsPlayed: 0,
    totalChartedSongs: 1,
    coverage: 0,
    rawSkillRating: 0.5,
    adjustedSkillRating: 0.5,
    adjustedSkillRank: 1,
    weightedRating: 0.5,
    weightedRank: 1,
    fcRate: 0,
    fcRateRank: 1,
    totalScore: 0,
    totalScoreRank: 1,
    maxScorePercent: 0,
    maxScorePercentRank: 1,
    avgAccuracy: 0,
    fullComboCount: 0,
    avgStars: 0,
    bestRank: 1,
    avgRank: 1,
    rawMaxScorePercent: null,
    rawWeightedRating: null,
    computedAt: new Date(0).toISOString(),
  };
}