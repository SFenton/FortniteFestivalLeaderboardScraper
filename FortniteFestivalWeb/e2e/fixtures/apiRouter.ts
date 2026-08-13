import type { Page, Route, WebSocketRoute } from '@playwright/test';
import type {
  BandRankingsPageResponse,
  ComboPageResponse,
  PathDataResponse,
  RankingsPageResponse,
  SoloFamilyPageResponse,
} from '@festival/core/api';
import {
  createEmptyScenario,
  E2E_COMBO_ID,
  E2E_NOW,
  E2E_PLAYER,
  type ApiOverride,
  type AppScenario,
} from './scenarios';

export type ApiRequestRecord = {
  method: string;
  path: string;
  search: string;
  headers: Record<string, string>;
  postData: string | null;
};

export class ApiScenarioController {
  readonly requests: ApiRequestRecord[] = [];
  socketConnections = 0;
  private scenario: AppScenario;
  private sockets = new Set<WebSocketRoute>();

  constructor(scenario = createEmptyScenario()) {
    this.scenario = structuredClone(scenario);
  }

  use(scenario: AppScenario): void {
    this.scenario = structuredClone(scenario);
    this.requests.length = 0;
  }

  current(): AppScenario {
    return this.scenario;
  }

  override(override: ApiOverride): void {
    this.scenario.overrides.push({ ...override });
  }

  count(path: string | RegExp, method?: string): number {
    return this.requests.filter(request => (
      matches(path, request.path)
      && (!method || request.method === method.toUpperCase())
    )).length;
  }

  last(path: string | RegExp, method?: string): ApiRequestRecord | undefined {
    for (let index = this.requests.length - 1; index >= 0; index -= 1) {
      const request = this.requests[index]!;
      if (
        matches(path, request.path)
        && (!method || request.method === method.toUpperCase())
      ) {
        return request;
      }
    }
    return undefined;
  }

  send(message: unknown): void {
    const payload = typeof message === 'string' ? message : JSON.stringify(message);
    for (const socket of this.sockets) socket.send(payload);
  }

  disconnect(code = 1012, reason = 'E2E reconnect'): void {
    for (const socket of this.sockets) socket.close({ code, reason });
    this.sockets.clear();
  }

  attachSocket(socket: WebSocketRoute): void {
    this.socketConnections += 1;
    this.sockets.add(socket);
    socket.onClose(() => this.sockets.delete(socket));
  }
}

export async function installScenarioApi(
  page: Page,
  scenario = createEmptyScenario(),
): Promise<ApiScenarioController> {
  const controller = new ApiScenarioController(scenario);

  await page.routeWebSocket('**/api/ws*', socket => {
    controller.attachSocket(socket);
  });

  await page.route('**/api/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (!url.pathname.startsWith('/api/')) {
      return route.continue();
    }
    const record: ApiRequestRecord = {
      method: request.method(),
      path: url.pathname,
      search: url.search,
      headers: await request.allHeaders(),
      postData: request.postData(),
    };
    controller.requests.push(record);

    const activeScenario = controller.current();
    const override = activeScenario.overrides.find(candidate => (
      (!candidate.method || candidate.method.toUpperCase() === record.method)
      && matches(candidate.path, record.path)
      && (candidate.remaining == null || candidate.remaining > 0)
    ));
    if (override) {
      if (override.remaining != null) override.remaining -= 1;
      if (override.delayMs) await delay(override.delayMs);
      return fulfill(route, activeScenario, override.body ?? {}, override.status);
    }

    return handleRoute(route, url, activeScenario);
  });

  return controller;
}

async function handleRoute(
  route: Route,
  url: URL,
  scenario: AppScenario,
): Promise<void> {
  const path = url.pathname;
  const method = route.request().method();

  if (path === '/api/publication') return fulfill(route, scenario, scenario.publication);
  if (path === '/api/features') return fulfill(route, scenario, scenario.features);
  if (path === '/api/service-info') return fulfill(route, scenario, scenario.serviceInfo);
  if (path === '/api/version') return fulfill(route, scenario, { version: 'e2e' });
  if (path === '/api/songs') {
    const ifNoneMatch = route.request().headers()['if-none-match'];
    if (ifNoneMatch === scenario.songsEtag) {
      return route.fulfill({
        status: 304,
        headers: responseHeaders(scenario, { ETag: scenario.songsEtag }),
      });
    }
    return fulfill(route, scenario, scenario.songs, 200, { ETag: scenario.songsEtag });
  }
  if (path === '/api/shop') return fulfill(route, scenario, scenario.shop);
  if (path === '/api/songs/member-score-filter') {
    return fulfill(route, scenario, {
      count: scenario.songs.songs.length,
      songIds: scenario.songs.songs.map(song => song.songId),
      hasAccountIds: splitParam(url, 'has'),
      missingAccountIds: splitParam(url, 'missing'),
      instruments: splitParam(url, 'instruments'),
    });
  }
  if (path === '/api/account/search') {
    return fulfill(route, scenario, {
      results: [
        E2E_PLAYER,
        { accountId: 'e2e-search-player', displayName: 'Search Result Player' },
      ],
    });
  }
  if (path === '/api/account/name-refresh') {
    return fulfill(route, scenario, {
      changed: 0,
      unchanged: 1,
      failed: 0,
      missing: 0,
      names: {},
      changedAccountIds: [],
    });
  }
  if (path === '/api/bands/search') {
    return fulfill(route, scenario, {
      ...scenario.bandSearch,
      query: url.searchParams.get('q') ?? scenario.bandSearch.query,
      normalizedQuery: (url.searchParams.get('q') ?? scenario.bandSearch.normalizedQuery).toLowerCase(),
      page: numberParam(url, 'page', 1),
      pageSize: numberParam(url, 'pageSize', 10),
    });
  }

  const leaderboardOffsetsMatch = /^\/api\/leaderboard-rank-offsets\/([^/]+)\/([^/]+)$/.exec(path);
  if (leaderboardOffsetsMatch) {
    return fulfill(route, scenario, {
      ...scenario.leaderboardOffsets,
      songId: decodeURIComponent(leaderboardOffsetsMatch[1]!),
      instrument: decodeURIComponent(leaderboardOffsetsMatch[2]!),
    });
  }
  const allBandLeaderboardsMatch = /^\/api\/leaderboard\/([^/]+)\/bands\/all$/.exec(path);
  if (allBandLeaderboardsMatch) {
    return fulfill(route, scenario, {
      ...scenario.allSongBandLeaderboards,
      songId: decodeURIComponent(allBandLeaderboardsMatch[1]!),
    });
  }
  const bandLeaderboardMatch = /^\/api\/leaderboard\/([^/]+)\/bands\/([^/]+)$/.exec(path);
  if (bandLeaderboardMatch) {
    return fulfill(route, scenario, {
      ...scenario.songBandLeaderboard,
      songId: decodeURIComponent(bandLeaderboardMatch[1]!),
      bandType: decodeURIComponent(bandLeaderboardMatch[2]!),
    });
  }
  const allLeaderboardsMatch = /^\/api\/leaderboard\/([^/]+)\/all$/.exec(path);
  if (allLeaderboardsMatch) {
    return fulfill(route, scenario, {
      ...scenario.allLeaderboards,
      songId: decodeURIComponent(allLeaderboardsMatch[1]!),
    });
  }
  const selectedScoresMatch = /^\/api\/leaderboard\/([^/]+)\/members\/scores$/.exec(path);
  if (selectedScoresMatch) {
    return fulfill(route, scenario, {
      ...scenario.selectedMemberScores,
      songId: decodeURIComponent(selectedScoresMatch[1]!),
    });
  }
  const leaderboardMatch = /^\/api\/leaderboard\/([^/]+)\/([^/]+)$/.exec(path);
  if (leaderboardMatch) {
    const offset = numberParam(url, 'offset', 0);
    const top = numberParam(url, 'top', scenario.leaderboard.entries.length);
    const entries = scenario.leaderboard.entries.slice(offset, offset + top);
    return fulfill(route, scenario, {
      ...scenario.leaderboard,
      songId: decodeURIComponent(leaderboardMatch[1]!),
      instrument: decodeURIComponent(leaderboardMatch[2]!),
      count: entries.length,
      entries,
    });
  }

  const playerNotificationsMatch = /^\/api\/player\/([^/]+)\/notifications$/.exec(path);
  if (playerNotificationsMatch) return fulfill(route, scenario, scenario.notifications);
  const playerSyncMatch = /^\/api\/player\/([^/]+)\/sync-status$/.exec(path);
  if (playerSyncMatch) {
    return fulfill(route, scenario, {
      ...scenario.syncStatus,
      accountId: decodeURIComponent(playerSyncMatch[1]!),
    });
  }
  const playerHistoryMatch = /^\/api\/player\/([^/]+)\/history$/.exec(path);
  if (playerHistoryMatch) {
    return fulfill(route, scenario, {
      ...scenario.playerHistory,
      accountId: decodeURIComponent(playerHistoryMatch[1]!),
    });
  }
  const playerStatsMatch = /^\/api\/player\/([^/]+)\/stats$/.exec(path);
  if (playerStatsMatch) {
    return fulfill(route, scenario, {
      ...scenario.playerStats,
      accountId: decodeURIComponent(playerStatsMatch[1]!),
    });
  }
  const playerBandsTypeMatch = /^\/api\/player\/([^/]+)\/bands\/([^/]+)$/.exec(path);
  if (playerBandsTypeMatch) {
    const bandType = decodeURIComponent(playerBandsTypeMatch[2]!);
    const key = bandType === 'Band_Duets'
      ? 'duos'
      : bandType === 'Band_Trios'
        ? 'trios'
        : 'quads';
    const group = scenario.playerBands[key];
    return fulfill(route, scenario, {
      accountId: decodeURIComponent(playerBandsTypeMatch[1]!),
      bandType,
      comboId: url.searchParams.get('combo'),
      totalCount: group.totalCount,
      entries: group.entries,
    });
  }
  const playerBandsMatch = /^\/api\/player\/([^/]+)\/bands$/.exec(path);
  if (playerBandsMatch) {
    const groupName = url.searchParams.get('group') ?? 'all';
    const group = scenario.playerBands[groupName as keyof typeof scenario.playerBands]
      ?? scenario.playerBands.all;
    return fulfill(route, scenario, {
      accountId: decodeURIComponent(playerBandsMatch[1]!),
      group: groupName,
      totalCount: group.totalCount,
      entries: group.entries,
    });
  }
  const playerTrackMatch = /^\/api\/player\/([^/]+)\/track$/.exec(path);
  if (playerTrackMatch) {
    return fulfill(route, scenario, {
      accountId: decodeURIComponent(playerTrackMatch[1]!),
      displayName: scenario.player.displayName,
      trackingStarted: method === 'POST',
      backfillStatus: 'complete',
      backfillKicked: false,
    });
  }
  const leaderboardRivalDetailMatch = /^\/api\/player\/([^/]+)\/leaderboard-rivals\/([^/]+)\/([^/]+)$/.exec(path);
  if (leaderboardRivalDetailMatch) return fulfill(route, scenario, scenario.rivalDetail);
  const leaderboardRivalsMatch = /^\/api\/player\/([^/]+)\/leaderboard-rivals\/([^/]+)$/.exec(path);
  if (leaderboardRivalsMatch) {
    return fulfill(route, scenario, {
      ...scenario.leaderboardRivals,
      instrument: decodeURIComponent(leaderboardRivalsMatch[2]!),
      rankBy: url.searchParams.get('rankBy') ?? scenario.leaderboardRivals.rankBy,
    });
  }
  const rivalsSuggestionsMatch = /^\/api\/player\/([^/]+)\/rivals\/suggestions$/.exec(path);
  if (rivalsSuggestionsMatch) return fulfill(route, scenario, scenario.rivalSuggestions);
  const rivalsAllMatch = /^\/api\/player\/([^/]+)\/rivals\/all$/.exec(path);
  if (rivalsAllMatch) return fulfill(route, scenario, scenario.rivalsAll);
  const rivalDetailMatch = /^\/api\/player\/([^/]+)\/rivals\/([^/]+)\/([^/]+)$/.exec(path);
  if (rivalDetailMatch) {
    return fulfill(route, scenario, {
      ...scenario.rivalDetail,
      combo: decodeURIComponent(rivalDetailMatch[2]!),
      rival: {
        ...scenario.rivalDetail.rival,
        accountId: decodeURIComponent(rivalDetailMatch[3]!),
      },
    });
  }
  const rivalsListMatch = /^\/api\/player\/([^/]+)\/rivals\/([^/]+)$/.exec(path);
  if (rivalsListMatch) {
    return fulfill(route, scenario, {
      ...scenario.rivalsList,
      combo: decodeURIComponent(rivalsListMatch[2]!),
    });
  }
  const rivalsOverviewMatch = /^\/api\/player\/([^/]+)\/rivals$/.exec(path);
  if (rivalsOverviewMatch) return fulfill(route, scenario, scenario.rivalsOverview);
  const playerExportMatch = /^\/api\/player\/([^/]+)\/export$/.exec(path);
  if (playerExportMatch) return fulfillDownload(route, `fst-export-${playerExportMatch[1]}.zip`);
  const playerMatch = /^\/api\/player\/([^/]+)$/.exec(path);
  if (playerMatch) {
    return fulfill(route, scenario, toWirePlayerResponse({
      ...scenario.player,
      accountId: decodeURIComponent(playerMatch[1]!),
    }));
  }

  const bandNotificationsMatch = /^\/api\/bands\/([^/]+)\/notifications$/.exec(path);
  if (bandNotificationsMatch) return fulfill(route, scenario, scenario.notifications);
  const bandSyncMatch = /^\/api\/bands\/([^/]+)\/([^/]+)\/sync-status$/.exec(path);
  if (bandSyncMatch) {
    return fulfill(route, scenario, {
      bandType: decodeURIComponent(bandSyncMatch[1]!),
      teamKey: decodeURIComponent(bandSyncMatch[2]!),
      isTracked: true,
      backfill: null,
      historyRecon: null,
    });
  }
  const bandExportMatch = /^\/api\/bands\/([^/]+)\/([^/]+)\/export$/.exec(path);
  if (bandExportMatch) return fulfillDownload(route, `fst-band-export-${bandExportMatch[1]}-${bandExportMatch[2]}.zip`);
  const bandDetailMatch = /^\/api\/bands\/([^/]+)$/.exec(path);
  if (bandDetailMatch) return fulfill(route, scenario, scenario.bandDetail);

  if (path === '/api/rankings/selected-members') {
    return fulfill(route, scenario, scenario.selectedMemberRankings);
  }
  if (path === '/api/rankings/composite') {
    return fulfill(route, scenario, withPage(scenario.compositeRankings, url));
  }
  const compositeNeighborhoodMatch = /^\/api\/rankings\/composite\/([^/]+)\/neighborhood$/.exec(path);
  if (compositeNeighborhoodMatch) {
    return fulfill(route, scenario, {
      accountId: decodeURIComponent(compositeNeighborhoodMatch[1]!),
      rank: 12,
      above: [],
      self: {
        accountId: E2E_PLAYER.accountId,
        displayName: E2E_PLAYER.displayName,
        compositeRating: 0.88,
        compositeRank: 12,
        instrumentsPlayed: 4,
        totalSongsPlayed: 10,
      },
      below: [],
    });
  }
  const compositePlayerMatch = /^\/api\/rankings\/composite\/([^/]+)$/.exec(path);
  if (compositePlayerMatch) {
    return fulfill(route, scenario, scenario.compositeRankings.entries[0] ?? {
      accountId: decodeURIComponent(compositePlayerMatch[1]!),
      displayName: scenario.player.displayName,
      instrumentsPlayed: 0,
      totalSongsPlayed: 0,
      compositeRating: 0,
      compositeRank: 0,
      instruments: {
        guitar: null,
        bass: null,
        drums: null,
        vocals: null,
        proGuitar: null,
        proBass: null,
      },
      computedAt: E2E_NOW,
    });
  }
  if (path === '/api/rankings/combo') {
    return fulfill(route, scenario, {
      comboId: url.searchParams.get('combo') ?? E2E_COMBO_ID,
      rankBy: url.searchParams.get('rankBy') ?? 'adjusted',
      page: numberParam(url, 'page', 1),
      pageSize: numberParam(url, 'pageSize', 10),
      totalAccounts: scenario.rankings.totalAccounts,
      entries: scenario.rankings.entries.map((entry, index) => ({
        rank: index + 1,
        accountId: entry.accountId,
        displayName: entry.displayName,
        adjustedRating: entry.adjustedSkillRating,
        weightedRating: entry.weightedRating,
        fcRate: entry.fcRate,
        totalScore: entry.totalScore,
        maxScorePercent: entry.maxScorePercent,
        songsPlayed: entry.songsPlayed,
        totalChartedSongs: entry.totalChartedSongs,
        fullComboCount: entry.fullComboCount,
        computedAt: entry.computedAt,
      })),
    } satisfies ComboPageResponse);
  }
  const comboPlayerMatch = /^\/api\/rankings\/combo\/([^/]+)$/.exec(path);
  if (comboPlayerMatch) {
    return fulfill(route, scenario, {
      comboId: url.searchParams.get('combo') ?? E2E_COMBO_ID,
      rankBy: url.searchParams.get('rankBy') ?? 'adjusted',
      totalAccounts: scenario.rankings.totalAccounts,
      rank: scenario.playerRanking.adjustedSkillRank,
      accountId: decodeURIComponent(comboPlayerMatch[1]!),
      displayName: scenario.player.displayName,
      adjustedRating: scenario.playerRanking.adjustedSkillRating,
      weightedRating: scenario.playerRanking.weightedRating,
      fcRate: scenario.playerRanking.fcRate,
      totalScore: scenario.playerRanking.totalScore,
      maxScorePercent: scenario.playerRanking.maxScorePercent,
      songsPlayed: scenario.playerRanking.songsPlayed,
      totalChartedSongs: scenario.playerRanking.totalChartedSongs,
      fullComboCount: scenario.playerRanking.fullComboCount,
      computedAt: scenario.playerRanking.computedAt,
    });
  }
  const familyPlayerMatch = /^\/api\/rankings\/family\/([^/]+)\/([^/]+)$/.exec(path);
  if (familyPlayerMatch) {
    return fulfill(route, scenario, {
      ...scenario.playerRanking,
      scopeId: decodeURIComponent(familyPlayerMatch[1]!),
      rankBy: url.searchParams.get('rankBy') ?? 'totalscore',
      totalRankedAccounts: scenario.rankings.totalAccounts,
    });
  }
  const familyMatch = /^\/api\/rankings\/family\/([^/]+)$/.exec(path);
  if (familyMatch) {
    return fulfill(route, scenario, {
      scopeId: decodeURIComponent(familyMatch[1]!) as SoloFamilyPageResponse['scopeId'],
      rankBy: url.searchParams.get('rankBy') ?? 'totalscore',
      page: numberParam(url, 'page', 1),
      pageSize: numberParam(url, 'pageSize', 10),
      totalAccounts: scenario.rankings.totalAccounts,
      entries: scenario.rankings.entries,
    } satisfies SoloFamilyPageResponse);
  }
  const bandCombosMatch = /^\/api\/rankings\/bands\/([^/]+)\/combos$/.exec(path);
  if (bandCombosMatch) {
    return fulfill(route, scenario, {
      ...scenario.bandCombos,
      bandType: decodeURIComponent(bandCombosMatch[1]!),
    });
  }
  const bandHistoryMatch = /^\/api\/rankings\/bands\/([^/]+)\/([^/]+)\/history$/.exec(path);
  if (bandHistoryMatch) return fulfill(route, scenario, scenario.bandRankHistory);
  const bandSongsMatch = /^\/api\/rankings\/bands\/([^/]+)\/([^/]+)\/songs$/.exec(path);
  if (bandSongsMatch) return fulfill(route, scenario, scenario.bandSongs);
  const bandRowsMatch = /^\/api\/rankings\/bands\/([^/]+)\/([^/]+)\/song-rows$/.exec(path);
  if (bandRowsMatch) return fulfill(route, scenario, scenario.bandSongRows);
  const bandRankingMatch = /^\/api\/rankings\/bands\/([^/]+)\/([^/]+)$/.exec(path);
  if (bandRankingMatch) return fulfill(route, scenario, scenario.bandRanking);
  const bandRankingsMatch = /^\/api\/rankings\/bands\/([^/]+)$/.exec(path);
  if (bandRankingsMatch) {
    return fulfill(route, scenario, {
      ...withPage(scenario.bandRankings, url),
      bandType: decodeURIComponent(bandRankingsMatch[1]!) as BandRankingsPageResponse['bandType'],
      comboId: url.searchParams.get('combo'),
      rankBy: (url.searchParams.get('rankBy') ?? scenario.bandRankings.rankBy) as BandRankingsPageResponse['rankBy'],
    } satisfies BandRankingsPageResponse);
  }
  const rankHistoryMatch = /^\/api\/rankings\/([^/]+)\/([^/]+)\/history$/.exec(path);
  if (rankHistoryMatch) {
    return fulfill(route, scenario, {
      ...scenario.rankHistory,
      instrument: decodeURIComponent(rankHistoryMatch[1]!),
      accountId: decodeURIComponent(rankHistoryMatch[2]!),
    });
  }
  const rankingNeighborhoodMatch = /^\/api\/rankings\/([^/]+)\/([^/]+)\/neighborhood$/.exec(path);
  if (rankingNeighborhoodMatch) {
    return fulfill(route, scenario, {
      ...scenario.leaderboardNeighborhood,
      instrument: decodeURIComponent(rankingNeighborhoodMatch[1]!),
      accountId: decodeURIComponent(rankingNeighborhoodMatch[2]!),
    });
  }
  const playerRankingMatch = /^\/api\/rankings\/([^/]+)\/([^/]+)$/.exec(path);
  if (playerRankingMatch) {
    return fulfill(route, scenario, {
      ...scenario.playerRanking,
      instrument: decodeURIComponent(playerRankingMatch[1]!),
      accountId: decodeURIComponent(playerRankingMatch[2]!),
    });
  }
  const rankingsMatch = /^\/api\/rankings\/([^/]+)$/.exec(path);
  if (rankingsMatch) {
    return fulfill(route, scenario, {
      ...withPage(scenario.rankings, url),
      instrument: decodeURIComponent(rankingsMatch[1]!),
      rankBy: url.searchParams.get('rankBy') ?? scenario.rankings.rankBy,
    } satisfies RankingsPageResponse);
  }

  if (/^\/api\/paths\/[^/]+\/[^/]+\/[^/]+\/data$/.test(path)) {
    return fulfill(route, scenario, {
      songName: 'Fixture Song',
      artist: 'Fixture Artist',
      charter: 'Fixture Charter',
      difficulty: 'expert',
      totalScore: 12_345,
      pathSummary: [
        'Optimising, please wait...',
        'Path: 2-2',
        'No SP score: 10,000',
        'Total score: 12,345',
        '2: 1 beats after NN (R)',
        '2: NN (B)',
      ].join('\n'),
      activations: [
        {
          startBeat: 20.99,
          endBeat: 36.99,
          startSeconds: 10.5,
        },
        {
          startBeat: 40,
          endBeat: 56,
          startSeconds: 20,
          scoreBeforeActivation: 5_000,
          startNotes: [{
            beat: 40,
            seconds: 20,
            cumulativeScore: 5_100,
            noteValue: 100,
            odPercent: 0.5,
            isSpGranting: false,
          }],
        },
      ],
      notes: [
        {
          beat: 20,
          seconds: 10,
          isSpNote: false,
          frets: { red: 2 },
        },
        {
          beat: 40,
          seconds: 20,
          isSpNote: false,
          frets: { blue: 0 },
        },
      ],
      spPhrases: [],
      measures: [],
      bpms: [],
      timeSignatures: [],
    } satisfies PathDataResponse);
  }
  if (/^\/api\/paths\/[^/]+\/[^/]+\/[^/]+$/.test(path)) {
    return route.fulfill({
      status: 200,
      contentType: 'image/svg+xml',
      headers: responseHeaders(scenario),
      body: '<svg xmlns="http://www.w3.org/2000/svg" width="320" height="180"><rect width="320" height="180" fill="#2d82e6"/></svg>',
    });
  }
  if (path === '/api/debug/client-interactions') {
    return fulfill(route, scenario, { accepted: true });
  }

  return route.fulfill({
    status: 500,
    contentType: 'application/json',
    headers: responseHeaders(scenario),
    body: JSON.stringify({
      error: `Unhandled deterministic e2e API route: ${method} ${path}${url.search}`,
    }),
  });
}

function withPage<T extends { page: number; pageSize: number }>(response: T, url: URL): T {
  return {
    ...response,
    page: numberParam(url, 'page', response.page),
    pageSize: numberParam(url, 'pageSize', response.pageSize),
  };
}

function fulfill(
  route: Route,
  scenario: AppScenario,
  body: unknown,
  status = 200,
  headers: Record<string, string> = {},
): Promise<void> {
  return route.fulfill({
    status,
    contentType: 'application/json',
    headers: responseHeaders(scenario, headers),
    body: JSON.stringify(body),
  });
}

function fulfillDownload(route: Route, fileName: string): Promise<void> {
  return route.fulfill({
    status: 200,
    contentType: 'application/zip',
    headers: {
      'Content-Disposition': `attachment; filename="${fileName}"`,
    },
    body: 'e2e-export',
  });
}

function responseHeaders(
  scenario: AppScenario,
  headers: Record<string, string> = {},
): Record<string, string> {
  return {
    'X-FST-Publication-Id': String(scenario.publication.publicationId),
    ...headers,
  };
}

function matches(pattern: string | RegExp, value: string): boolean {
  return typeof pattern === 'string' ? pattern === value : pattern.test(value);
}

function splitParam(url: URL, name: string): string[] {
  return (url.searchParams.get(name) ?? '')
    .split(',')
    .map(value => value.trim())
    .filter(Boolean);
}

function numberParam(url: URL, name: string, fallback: number): number {
  const value = Number(url.searchParams.get(name));
  return Number.isFinite(value) ? value : fallback;
}

function delay(milliseconds: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, milliseconds));
}

function toWirePlayerResponse(player: AppScenario['player']) {
  return {
    accountId: player.accountId,
    displayName: player.displayName,
    totalScores: player.totalScores,
    status: player.status,
    notYetPublished: player.notYetPublished,
    scores: player.scores.map(score => ({
      si: score.songId,
      ins: instrumentHex(score.instrument),
      sc: score.score,
      acc: (score.accuracy ?? 0) / 1000,
      fc: score.isFullCombo ?? false,
      st: score.stars ?? 0,
      dif: score.difficulty ?? 0,
      sn: score.season ?? 0,
      pct: score.percentile ?? 0,
      rk: score.rank,
      lrk: score.localRank,
      et: score.endTime,
      te: score.totalEntries ?? 0,
      lp: score.lastPlayedAt,
      vlp: score.validLastPlayedAt,
      ml: score.minLeeway,
      vs: score.validScores?.map(validScore => ({
        sc: validScore.score,
        acc: validScore.accuracy != null ? validScore.accuracy / 1000 : validScore.accuracy,
        fc: validScore.fc,
        st: validScore.stars,
        ml: validScore.minLeeway,
        rt: validScore.rankTiers?.map(tier => ({ l: tier.leeway, r: tier.rank })),
      })),
      isValid: score.isValid,
      validScore: score.validScore,
      validRank: score.validRank,
      validAccuracy: score.validAccuracy != null ? score.validAccuracy / 1000 : score.validAccuracy,
      validIsFullCombo: score.validIsFullCombo,
      validStars: score.validStars,
      validTotalEntries: score.validTotalEntries,
    })),
  };
}

function instrumentHex(instrument: string): string {
  const keys = [
    'Solo_Guitar',
    'Solo_Bass',
    'Solo_Drums',
    'Solo_Vocals',
    'Solo_PeripheralGuitar',
    'Solo_PeripheralBass',
    'Solo_PeripheralVocals',
    'Solo_PeripheralCymbals',
    'Solo_PeripheralDrums',
  ];
  const bit = keys.indexOf(instrument);
  if (bit < 0) throw new Error(`Unsupported e2e instrument: ${instrument}`);
  return (1 << bit).toString(16).padStart(2, '0');
}
