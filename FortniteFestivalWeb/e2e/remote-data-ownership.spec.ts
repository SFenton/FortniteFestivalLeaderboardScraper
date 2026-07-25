import { expect, test, type Page, type Route } from '@playwright/test';

const SONG_ID = 'song-cache-test';
const PROFILE_A = { accountId: 'profile-a', displayName: 'Profile A' };
const PROFILE_B = { accountId: 'profile-b', displayName: 'Profile B' };

type RequestCounts = Map<string, number>;

function incrementRequest(counts: RequestCounts, path: string) {
  counts.set(path, (counts.get(path) ?? 0) + 1);
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(body),
  });
}

function playerResponse(accountId: string) {
  const displayName = accountId === PROFILE_B.accountId ? PROFILE_B.displayName : PROFILE_A.displayName;
  return {
    accountId,
    displayName,
    totalScores: 1,
    scores: [{
      si: SONG_ID,
      ins: '01',
      sc: 100_000,
      acc: 980,
      fc: true,
      st: 5,
      dif: 1,
      sn: 1,
      pct: 90,
      rk: 2,
      te: 10,
    }],
  };
}

function rankingEntry(accountId: string, rank: number) {
  return {
    accountId,
    displayName: accountId === PROFILE_B.accountId ? PROFILE_B.displayName : PROFILE_A.displayName,
    adjustedSkillRating: 0.8,
    adjustedSkillRank: rank,
    weightedRank: rank,
    fcRateRank: rank,
    totalScoreRank: rank,
    maxScorePercentRank: rank,
    rawSkillRating: 0.8,
    weightedRating: 0.7,
    rawWeightedRating: 0.7,
    totalChartedSongs: 1,
    songsPlayed: 1,
    totalScore: 100_000,
    maxScorePercent: 0.9,
    rawMaxScorePercent: 0.9,
    fullComboCount: 1,
    fcRate: 1,
    avgAccuracy: 98,
    avgStars: 5,
    bestRank: rank,
    avgRank: rank,
    coverage: 1,
    computedAt: '2026-07-25T00:00:00Z',
  };
}

async function installApi(page: Page, counts: RequestCounts) {
  await page.routeWebSocket('**/api/ws', () => {});
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url());
    if (!url.pathname.startsWith('/api/')) {
      return route.continue();
    }
    const path = `${url.pathname}${url.search}`;
    incrementRequest(counts, path);

    if (url.pathname === '/api/features') {
      return json(route, {
        compete: true,
        leaderboards: true,
        difficulty: false,
        playerBands: false,
        experimentalRanks: true,
        appManual: false,
      });
    }
    if (url.pathname === '/api/service-info') {
      return json(route, {
        lastCompletedUpdate: null,
        currentUpdate: null,
        activeScrapeId: null,
        publishedScrapeId: 1,
        publication: { publishedScrapeId: 1, publishedAt: '2026-07-25T00:00:00Z', publicReadsFrozen: false },
        workerStatus: { status: 'offline', rawStatus: 'offline' },
      });
    }
    if (url.pathname === '/api/songs') {
      return json(route, {
        count: 1,
        currentSeason: 1,
        songs: [{
          songId: SONG_ID,
          title: 'Cache Test Song',
          artist: 'Cache Artist',
          year: 2026,
          albumArt: 'https://example.com/cache-song.jpg',
          maxScores: { Solo_Guitar: 100_000 },
        }],
      });
    }
    if (url.pathname === '/api/shop') return json(route, { songs: [], newSongs: [] });
    if (url.pathname === '/api/version') return json(route, { version: 'test' });
    if (url.pathname === '/api/account/name-refresh') {
      return json(route, {
        changed: 0,
        unchanged: 1,
        failed: 0,
        missing: 0,
        names: {},
        changedAccountIds: [],
      });
    }

    const syncMatch = /^\/api\/player\/([^/]+)\/sync-status$/.exec(url.pathname);
    if (syncMatch) {
      return json(route, {
        accountId: syncMatch[1],
        isTracked: true,
        backfill: null,
        historyRecon: null,
      });
    }
    const notificationsMatch = /^\/api\/player\/([^/]+)\/notifications$/.exec(url.pathname);
    if (notificationsMatch) {
      return json(route, {
        generatedAt: '2026-07-25T00:00:00Z',
        expiresAfterHours: 24,
        items: [],
      });
    }
    const historyMatch = /^\/api\/player\/([^/]+)\/history$/.exec(url.pathname);
    if (historyMatch) return json(route, { accountId: historyMatch[1], count: 0, history: [] });
    const statsMatch = /^\/api\/player\/([^/]+)\/stats$/.exec(url.pathname);
    if (statsMatch) {
      return json(route, {
        accountId: statsMatch[1],
        totalSongs: 1,
        instruments: [],
        compositeRanks: null,
        familyRanks: null,
        instrumentRanks: [],
        bands: null,
      });
    }
    const bandsMatch = /^\/api\/player\/([^/]+)\/bands$/.exec(url.pathname);
    if (bandsMatch) {
      return json(route, {
        accountId: bandsMatch[1],
        group: url.searchParams.get('group') ?? 'all',
        totalCount: 0,
        entries: [],
      });
    }
    const trackMatch = /^\/api\/player\/([^/]+)\/track$/.exec(url.pathname);
    if (trackMatch) {
      return json(route, {
        accountId: trackMatch[1],
        displayName: trackMatch[1] === PROFILE_B.accountId ? PROFILE_B.displayName : PROFILE_A.displayName,
        trackingStarted: false,
        backfillStatus: 'complete',
        backfillKicked: false,
      });
    }
    const leaderboardRivalsMatch = /^\/api\/player\/([^/]+)\/leaderboard-rivals\/([^/]+)$/.exec(url.pathname);
    if (leaderboardRivalsMatch) {
      return json(route, {
        instrument: leaderboardRivalsMatch[2],
        above: [],
        below: [],
      });
    }
    const rivalsMatch = /^\/api\/player\/([^/]+)\/rivals\/([^/]+)$/.exec(url.pathname);
    if (rivalsMatch) {
      return json(route, {
        combo: rivalsMatch[2],
        above: [{
          accountId: `above-${rivalsMatch[1]}`,
          displayName: `Above ${rivalsMatch[1]}`,
          sharedSongCount: 1,
          rivalScore: 100,
          aheadCount: 1,
          behindCount: 0,
          avgSignedDelta: 1,
        }],
        below: [],
      });
    }
    const playerMatch = /^\/api\/player\/([^/]+)$/.exec(url.pathname);
    if (playerMatch) return json(route, playerResponse(playerMatch[1]));

    const playerRankingMatch = /^\/api\/rankings\/Solo_Guitar\/([^/]+)$/.exec(url.pathname);
    if (playerRankingMatch) {
      return json(route, {
        instrument: 'Solo_Guitar',
        totalRankedAccounts: 10,
        ...rankingEntry(playerRankingMatch[1], 2),
      });
    }
    if (url.pathname === '/api/rankings/Solo_Guitar') {
      return json(route, {
        instrument: 'Solo_Guitar',
        rankBy: 'totalscore',
        page: 1,
        pageSize: 10,
        totalAccounts: 10,
        entries: [rankingEntry('top-player', 1)],
      });
    }
    if (url.pathname === '/api/rankings/combo') {
      return json(route, {
        comboId: url.searchParams.get('combo') ?? '0',
        rankBy: 'totalscore',
        page: 1,
        pageSize: 10,
        totalAccounts: 0,
        entries: [],
      });
    }
    const comboPlayerMatch = /^\/api\/rankings\/combo\/([^/]+)$/.exec(url.pathname);
    if (comboPlayerMatch) {
      return json(route, {
        comboId: url.searchParams.get('combo') ?? '0',
        rankBy: 'totalscore',
        totalAccounts: 10,
        rank: 2,
        accountId: comboPlayerMatch[1],
        displayName: comboPlayerMatch[1],
        adjustedRating: 0.5,
        weightedRating: 0.5,
        fcRate: 1,
        totalScore: 100_000,
        maxScorePercent: 0.9,
        songsPlayed: 1,
        fullComboCount: 1,
        computedAt: '2026-07-25T00:00:00Z',
      });
    }

    if (url.pathname === `/api/leaderboard/${SONG_ID}/Solo_Guitar`) {
      return json(route, {
        songId: SONG_ID,
        instrument: 'Solo_Guitar',
        count: 25,
        totalEntries: 50,
        localEntries: 50,
        entries: Array.from({ length: 25 }, (_, index) => ({
          accountId: index === 0 ? 'top-player' : `player-${index + 1}`,
          displayName: index === 0 ? 'Top Player' : `Player ${index + 1}`,
          score: 100_000 - index,
          rank: index + 1,
          accuracy: 1_000_000,
          isFullCombo: true,
          stars: 5,
          season: 1,
        })),
      });
    }
    if (url.pathname === `/api/leaderboard/${SONG_ID}/all`) {
      return json(route, {
        songId: SONG_ID,
        instruments: [{
          instrument: 'Solo_Guitar',
          count: 1,
          totalEntries: 1,
          localEntries: 1,
          entries: [{
            accountId: 'top-player',
            displayName: 'Top Player',
            score: 100_000,
            rank: 1,
          }],
        }],
      });
    }
    if (url.pathname === `/api/leaderboard/${SONG_ID}/bands/all`) {
      return json(route, { songId: SONG_ID, bands: [] });
    }
    if (url.pathname === `/api/leaderboard/${SONG_ID}/members/scores`) {
      return json(route, { songId: SONG_ID, scores: [] });
    }

    return json(route, {});
  });
}

async function selectProfile(page: Page, profile: typeof PROFILE_A) {
  await page.evaluate(profileValue => {
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', ...profileValue }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify(profileValue));
    window.dispatchEvent(new Event('fst:selectedProfileChanged'));
    window.dispatchEvent(new Event('fst:trackedPlayerChanged'));
  }, profile);
}

async function navigate(page: Page, path: string) {
  await page.evaluate(nextPath => {
    window.location.hash = `#${nextPath}`;
  }, path);
  await page.waitForFunction(nextPath => window.location.hash === `#${nextPath}`, path);
}

test('React Query owns remote data across Player, Leaderboard, Rivals, and Compete navigation', async ({ page }) => {
  const counts: RequestCounts = new Map();
  await installApi(page, counts);
  await page.goto('/', { waitUntil: 'domcontentloaded' });
  await page.evaluate(() => {
    for (const key of Object.keys(localStorage)) {
      if (key.startsWith('fst:')) localStorage.removeItem(key);
    }
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));
    const profile = { accountId: 'profile-a', displayName: 'Profile A' };
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', ...profile }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify(profile));
  });
  await page.reload({ waitUntil: 'domcontentloaded' });

  await navigate(page, `/player/${PROFILE_A.accountId}`);
  await expect(page.getByText(PROFILE_A.displayName).first()).toBeVisible();
  const playerRequestsAfterPlayerRoute = counts.get(`/api/player/${PROFILE_A.accountId}`) ?? 0;
  expect(playerRequestsAfterPlayerRoute).toBeGreaterThan(0);
  await navigate(page, `/songs/${SONG_ID}/Solo_Guitar`);
  await expect(page.getByText('Top Player')).toBeVisible();
  await page.getByRole('button', { name: 'Next' }).click();
  await expect(page.getByTestId('leaderboard-page-info')).toHaveText('2 / 2');
  await navigate(page, '/rivals');
  await expect(page.getByText(`Above ${PROFILE_A.accountId}`).first()).toBeVisible();
  await page.goBack();
  await expect(page.getByText('Top Player')).toBeVisible();
  await expect(page.getByTestId('leaderboard-page-info')).toHaveText('2 / 2');

  await navigate(page, '/compete');
  await expect(page.getByText(`Above ${PROFILE_A.accountId}`).first()).toBeVisible();
  await navigate(page, '/rivals');
  await expect(page.getByText(`Above ${PROFILE_A.accountId}`).first()).toBeVisible();

  expect(counts.get(`/api/player/${PROFILE_A.accountId}`)).toBe(playerRequestsAfterPlayerRoute);
  expect(counts.get(`/api/leaderboard/${SONG_ID}/Solo_Guitar?top=25&offset=0`)).toBe(1);
  expect(counts.get(`/api/leaderboard/${SONG_ID}/Solo_Guitar?top=25&offset=25`)).toBe(1);
  expect(counts.get(`/api/player/${PROFILE_A.accountId}/rivals/Solo_Guitar`)).toBe(1);

  await selectProfile(page, PROFILE_B);
  await navigate(page, '/compete');
  await expect(page.getByText(`Above ${PROFILE_B.accountId}`).first()).toBeVisible();
  expect(counts.get(`/api/player/${PROFILE_B.accountId}/rivals/Solo_Guitar`)).toBe(1);

  await selectProfile(page, PROFILE_A);
  await navigate(page, '/rivals');
  await expect(page.getByText(`Above ${PROFILE_A.accountId}`).first()).toBeVisible();
  expect(counts.get(`/api/player/${PROFILE_A.accountId}/rivals/Solo_Guitar`)).toBe(1);
});
