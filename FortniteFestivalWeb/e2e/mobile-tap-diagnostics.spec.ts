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
});

async function rapidTab(page: Page, testInfo: TestInfo, tabKey: string, expectedRoute?: string) {
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