import type { Page, Route } from '@playwright/test';

export const E2E_PLAYER = {
  accountId: '195e93ef108143b2975ee46662d4d0e1',
  displayName: 'SFentonX',
};
export const E2E_BAND = {
  bandId: 'e2e-band',
  bandType: 'Band_Duets' as const,
  teamKey: 'e2e-player-a:e2e-player-b',
  displayName: 'E2E Duo',
};
export const E2E_SONG_ID = 'e2e-song';

const E2E_TIMESTAMP = '2026-01-01T00:00:00.000Z';

export async function installDeterministicApiMocks(page: Page) {
  await page.routeWebSocket('**/api/ws*', () => {});
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    const path = url.pathname;
    if (!path.startsWith('/api/')) return route.continue();

    if (path === '/api/service-info') {
      return json(route, {
        lastCompletedUpdate: {
          scrapeId: 1,
          startedAt: E2E_TIMESTAMP,
          completedAt: E2E_TIMESTAMP,
          publishedAt: E2E_TIMESTAMP,
        },
        currentUpdate: {
          status: 'idle',
          startedAt: null,
          phase: null,
          subOperation: null,
        },
        activeScrapeId: null,
        publishedScrapeId: 1,
        publication: {
          publishedScrapeId: 1,
          publishedAt: E2E_TIMESTAMP,
          publicReadsFrozen: false,
        },
        workerStatus: { status: 'idle', rawStatus: 'idle' },
      });
    }
    if (path === '/api/songs') return json(route, songsResponse());
    if (path === '/api/shop') {
      return json(route, { songs: [], newSongs: [], lastUpdated: E2E_TIMESTAMP });
    }
    if (path === '/api/version') return json(route, { version: 'e2e' });
    if (path === '/api/account/name-refresh') {
      return json(route, {
        changed: 0,
        unchanged: 1,
        failed: 0,
        missing: 0,
        names: {},
        changedAccountIds: [],
      });
    }
    if (path === '/api/account/search') {
      return json(route, { results: [E2E_PLAYER] });
    }
    if (path === '/api/bands/search') {
      return json(route, {
        query: url.searchParams.get('q') ?? '',
        normalizedQuery: url.searchParams.get('q') ?? '',
        rankBy: 'adjusted',
        page: 1,
        pageSize: 10,
        totalCount: 0,
        isAmbiguous: false,
        needsDisambiguation: false,
        interpretations: [],
        results: [],
      });
    }
    const bandDetailMatch = /^\/api\/bands\/([^/]+)$/.exec(path);
    if (bandDetailMatch) {
      return json(route, {
        band: {
          bandId: bandDetailMatch[1],
          bandType: E2E_BAND.bandType,
          teamKey: E2E_BAND.teamKey,
          appearanceCount: 2,
          members: [
            { accountId: 'e2e-player-a', displayName: 'E2E Player A', instruments: ['Solo_Guitar'] },
            { accountId: 'e2e-player-b', displayName: 'E2E Player B', instruments: ['Solo_Bass'] },
          ],
        },
        ranking: bandRanking(),
        configurations: [],
      });
    }
    if (path === '/api/songs/member-score-filter') {
      return json(route, { count: 0, songIds: [] });
    }

    const syncMatch = /^\/api\/player\/([^/]+)\/sync-status$/.exec(path);
    if (syncMatch) {
      return json(route, {
        accountId: syncMatch[1],
        isTracked: true,
        backfill: null,
        historyRecon: null,
      });
    }
    const notificationsMatch = /^\/api\/player\/([^/]+)\/notifications$/.exec(path);
    if (notificationsMatch) {
      return json(route, {
        generatedAt: E2E_TIMESTAMP,
        sourceRunId: 1,
        sourceCompletedAt: E2E_TIMESTAMP,
        notificationsGenerated: true,
        items: [],
      });
    }
    const historyMatch = /^\/api\/player\/([^/]+)\/history$/.exec(path);
    if (historyMatch) {
      return json(route, { accountId: historyMatch[1], count: 0, history: [] });
    }
    const statsMatch = /^\/api\/player\/([^/]+)\/stats$/.exec(path);
    if (statsMatch) {
      return json(route, {
        accountId: statsMatch[1],
        totalSongs: 0,
        instruments: [],
        compositeRanks: null,
        familyRanks: null,
        instrumentRanks: [],
        bands: null,
      });
    }
    const bandsMatch = /^\/api\/player\/([^/]+)\/bands$/.exec(path);
    if (bandsMatch) {
      return json(route, {
        accountId: bandsMatch[1],
        group: url.searchParams.get('group') ?? 'all',
        totalCount: 0,
        entries: [],
      });
    }
    const trackMatch = /^\/api\/player\/([^/]+)\/track$/.exec(path);
    if (trackMatch) {
      return json(route, {
        accountId: trackMatch[1],
        displayName: displayNameFor(trackMatch[1]),
        trackingStarted: false,
        backfillStatus: 'complete',
        backfillKicked: false,
      });
    }
    const rivalsAllMatch = /^\/api\/player\/([^/]+)\/rivals\/all$/.exec(path);
    if (rivalsAllMatch) {
      return json(route, { accountId: rivalsAllMatch[1], songs: [], combos: [] });
    }
    const rivalSuggestionsMatch = /^\/api\/player\/([^/]+)\/rivals\/suggestions$/.exec(path);
    if (rivalSuggestionsMatch) {
      return json(route, {
        accountId: rivalSuggestionsMatch[1],
        combo: url.searchParams.get('combo') ?? '',
        computedAt: E2E_TIMESTAMP,
        rivals: [],
      });
    }
    const leaderboardRivalsMatch = /^\/api\/player\/([^/]+)\/leaderboard-rivals\/([^/]+)$/.exec(path);
    if (leaderboardRivalsMatch) {
      return json(route, {
        instrument: leaderboardRivalsMatch[2],
        rankBy: url.searchParams.get('rankBy') ?? 'totalscore',
        userRank: null,
        above: [],
        below: [],
      });
    }
    const rivalsOverviewMatch = /^\/api\/player\/([^/]+)\/rivals$/.exec(path);
    if (rivalsOverviewMatch) {
      return json(route, {
        accountId: rivalsOverviewMatch[1],
        computedAt: E2E_TIMESTAMP,
        combos: [],
      });
    }
    const rivalsMatch = /^\/api\/player\/([^/]+)\/rivals\/([^/]+)$/.exec(path);
    if (rivalsMatch) {
      return json(route, { combo: rivalsMatch[2], above: [], below: [] });
    }
    const playerMatch = /^\/api\/player\/([^/]+)$/.exec(path);
    if (playerMatch) {
      return json(route, {
        accountId: playerMatch[1],
        displayName: displayNameFor(playerMatch[1]),
        totalScores: 0,
        scores: [],
      });
    }

    if (path === '/api/rankings/selected-members') {
      return json(route, { instruments: [] });
    }
    if (path === '/api/rankings/composite') {
      return json(route, { page: 1, pageSize: 10, totalAccounts: 0, entries: [] });
    }
    if (path === '/api/rankings/combo') {
      return json(route, {
        comboId: url.searchParams.get('combo') ?? '',
        rankBy: url.searchParams.get('rankBy') ?? 'totalscore',
        page: 1,
        pageSize: 10,
        totalAccounts: 0,
        entries: [],
      });
    }
    const comboPlayerMatch = /^\/api\/rankings\/combo\/([^/]+)$/.exec(path);
    if (comboPlayerMatch) {
      return json(route, comboRankingEntry(comboPlayerMatch[1], url.searchParams.get('combo') ?? ''));
    }
    const bandCombosMatch = /^\/api\/rankings\/bands\/([^/]+)\/combos$/.exec(path);
    if (bandCombosMatch) {
      return json(route, { bandType: bandCombosMatch[1], combos: [] });
    }
    const bandHistoryMatch = /^\/api\/rankings\/bands\/([^/]+)\/([^/]+)\/history$/.exec(path);
    if (bandHistoryMatch) {
      return json(route, {
        bandType: bandHistoryMatch[1],
        teamKey: decodeURIComponent(bandHistoryMatch[2]!),
        comboId: url.searchParams.get('combo'),
        days: Number(url.searchParams.get('days') ?? 30),
        historyStatus: 'current',
        history: [
          {
            snapshotDate: '2025-12-31',
            snapshotTakenAt: '2025-12-31T00:00:00.000Z',
            adjustedSkillRank: 8,
            weightedRank: 9,
            fcRateRank: 10,
            totalScoreRank: 11,
            adjustedSkillRating: 0.7,
            rawSkillRating: 0.72,
            weightedRating: 0.65,
            rawWeightedRating: 0.66,
            fcRate: 0.4,
            totalScore: 90_000,
            songsPlayed: 1,
            coverage: 1,
            fullComboCount: 0,
            totalChartedSongs: 1,
            totalRankedTeams: 20,
          },
          {
            snapshotDate: '2026-01-01',
            snapshotTakenAt: E2E_TIMESTAMP,
            adjustedSkillRank: 7,
            weightedRank: 8,
            fcRateRank: 9,
            totalScoreRank: 10,
            adjustedSkillRating: 0.8,
            rawSkillRating: 0.82,
            weightedRating: 0.75,
            rawWeightedRating: 0.76,
            fcRate: 1,
            totalScore: 100_000,
            songsPlayed: 1,
            coverage: 1,
            fullComboCount: 1,
            totalChartedSongs: 1,
            totalRankedTeams: 20,
          },
        ],
      });
    }
    const bandSongsMatch = /^\/api\/rankings\/bands\/([^/]+)\/([^/]+)\/songs$/.exec(path);
    if (bandSongsMatch) {
      return json(route, {
        bandType: bandSongsMatch[1],
        teamKey: decodeURIComponent(bandSongsMatch[2]!),
        comboId: url.searchParams.get('combo'),
        limit: Number(url.searchParams.get('limit') ?? 5),
        best: [],
        worst: [],
      });
    }
    const bandRankingMatch = /^\/api\/rankings\/bands\/([^/]+)\/([^/]+)$/.exec(path);
    if (bandRankingMatch) {
      return json(route, {
        ...bandRanking(),
        bandType: bandRankingMatch[1],
        teamKey: decodeURIComponent(bandRankingMatch[2]!),
      });
    }
    const bandRankingsMatch = /^\/api\/rankings\/bands\/([^/]+)$/.exec(path);
    if (bandRankingsMatch) {
      return json(route, {
        bandType: bandRankingsMatch[1],
        comboId: url.searchParams.get('combo'),
        rankBy: url.searchParams.get('rankBy') ?? 'adjusted',
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 10),
        totalTeams: 0,
        entries: [],
      });
    }
    const playerRankingMatch = /^\/api\/rankings\/([^/]+)\/([^/]+)$/.exec(path);
    if (playerRankingMatch) {
      return json(route, accountRanking(playerRankingMatch[2], playerRankingMatch[1]));
    }
    const rankingsMatch = /^\/api\/rankings\/([^/]+)$/.exec(path);
    if (rankingsMatch) {
      return json(route, {
        instrument: rankingsMatch[1],
        rankBy: url.searchParams.get('rankBy') ?? 'totalscore',
        page: Number(url.searchParams.get('page') ?? 1),
        pageSize: Number(url.searchParams.get('pageSize') ?? 10),
        totalAccounts: 0,
        entries: [],
      });
    }

    const allLeaderboardsMatch = /^\/api\/leaderboard\/([^/]+)\/all$/.exec(path);
    if (allLeaderboardsMatch) {
      return json(route, { songId: allLeaderboardsMatch[1], instruments: [] });
    }
    const memberScoresMatch = /^\/api\/leaderboard\/([^/]+)\/members\/scores$/.exec(path);
    if (memberScoresMatch) {
      return json(route, { songId: memberScoresMatch[1], scores: [] });
    }
    const allBandLeaderboardsMatch = /^\/api\/leaderboard\/([^/]+)\/bands\/all$/.exec(path);
    if (allBandLeaderboardsMatch) {
      return json(route, { songId: allBandLeaderboardsMatch[1], bands: [] });
    }

    return route.fulfill({
      status: 500,
      contentType: 'application/json',
      body: JSON.stringify({ error: `Unhandled deterministic e2e API route: ${path}` }),
    });
  });
}

function songsResponse() {
  return {
    count: 1,
    currentSeason: 1,
    songs: [{
      songId: E2E_SONG_ID,
      title: 'Deterministic E2E Song',
      artist: 'Festival QA',
      year: 2026,
      durationSeconds: 180,
      difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1 },
      maxScores: {
        Solo_Guitar: 100_000,
        Solo_Bass: 90_000,
        Solo_Drums: 110_000,
        Solo_Vocals: 80_000,
      },
    }],
  };
}

function displayNameFor(accountId: string) {
  return accountId === E2E_PLAYER.accountId ? E2E_PLAYER.displayName : `Player ${accountId.slice(0, 8)}`;
}

function accountRanking(accountId: string, instrument: string) {
  return {
    instrument,
    totalRankedAccounts: 1,
    rank: 1,
    accountId,
    displayName: displayNameFor(accountId),
    adjustedSkillRating: 0,
    adjustedSkillRank: 1,
    weightedRank: 1,
    fcRateRank: 1,
    totalScoreRank: 1,
    maxScorePercentRank: 1,
    rawSkillRating: 0,
    weightedRating: 0,
    rawWeightedRating: 0,
    totalChartedSongs: 1,
    songsPlayed: 0,
    totalScore: 0,
    maxScorePercent: 0,
    rawMaxScorePercent: 0,
    fullComboCount: 0,
    fcRate: 0,
    avgAccuracy: 0,
    avgStars: 0,
    bestRank: 1,
    avgRank: 1,
    coverage: 0,
    computedAt: E2E_TIMESTAMP,
  };
}

function comboRankingEntry(accountId: string, comboId: string) {
  return {
    comboId,
    rankBy: 'totalscore',
    totalAccounts: 1,
    rank: 1,
    accountId,
    displayName: displayNameFor(accountId),
    adjustedRating: 0,
    weightedRating: 0,
    fcRate: 0,
    totalScore: 0,
    maxScorePercent: 0,
    songsPlayed: 0,
    fullComboCount: 0,
    computedAt: E2E_TIMESTAMP,
  };
}

function bandRanking() {
  return {
    bandId: E2E_BAND.bandId,
    bandType: E2E_BAND.bandType,
    teamKey: E2E_BAND.teamKey,
    teamMembers: [
      { accountId: 'e2e-player-a', displayName: 'E2E Player A' },
      { accountId: 'e2e-player-b', displayName: 'E2E Player B' },
    ],
    members: [
      { accountId: 'e2e-player-a', displayName: 'E2E Player A', instruments: ['Solo_Guitar'] },
      { accountId: 'e2e-player-b', displayName: 'E2E Player B', instruments: ['Solo_Bass'] },
    ],
    songsPlayed: 1,
    totalChartedSongs: 1,
    coverage: 1,
    rawSkillRating: 0.82,
    adjustedSkillRating: 0.8,
    adjustedSkillRank: 7,
    weightedRating: 0.75,
    rawWeightedRating: 0.76,
    weightedRank: 8,
    fcRate: 1,
    fcRateRank: 9,
    totalScore: 100_000,
    totalScoreRank: 10,
    avgAccuracy: 1_000_000,
    fullComboCount: 1,
    avgStars: 6,
    bestRank: 1,
    avgRank: 1,
    computedAt: E2E_TIMESTAMP,
    totalRankedTeams: 20,
    configurations: [],
  };
}

function json(route: Route, body: unknown) {
  return route.fulfill({
    status: 200,
    contentType: 'application/json',
    headers: { 'X-FST-Publication-Id': '1' },
    body: JSON.stringify(body),
  });
}
