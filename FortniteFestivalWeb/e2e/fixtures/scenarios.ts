import type {
  AccountRankingDto,
  AccountRankingEntry,
  AllLeaderboardsResponse,
  AllSongBandLeaderboardsResponse,
  BandComboCatalogResponse,
  BandDetailResponse,
  BandRankHistoryResponse,
  BandRankingDto,
  BandRankingsPageResponse,
  BandSearchResponse,
  BandSongRowsResponse,
  BandSongsResponse,
  CompositePageResponse,
  FeatureFlagsResponse,
  ImprovementNotificationsEnvelope,
  LeaderboardNeighborhoodResponse,
  LeaderboardRankOffsetsResponse,
  LeaderboardResponse,
  LeaderboardRivalsListResponse,
  PlayerBandsResponse,
  PlayerHistoryResponse,
  PlayerResponse,
  PlayerStatsResponse,
  PublicationResponse,
  RankHistoryResponse,
  RankingsPageResponse,
  RivalDetailResponse,
  RivalsAllResponse,
  RivalsListResponse,
  RivalsOverviewResponse,
  RivalSuggestionsResponse,
  SelectedMemberRankingsResponse,
  SelectedMemberSongScoresResponse,
  ServiceInfoResponse,
  ShopResponse,
  SongBandLeaderboardResponse,
  SongsResponse,
  SyncStatusResponse,
} from '@festival/core/api';

export const E2E_NOW = '2026-01-01T12:00:00.000Z';
export const E2E_PLAYER = {
  accountId: '195e93ef108143b2975ee46662d4d0e1',
  displayName: 'SFentonX',
} as const;
export const E2E_RIVAL = {
  accountId: 'e2e-rival',
  displayName: 'Rival Player',
} as const;
export const E2E_BAND = {
  bandId: 'e2e-band',
  bandType: 'Band_Duets' as const,
  teamKey: 'e2e-player-a:e2e-player-b',
  displayName: 'E2E Duo',
  members: [
    { accountId: 'e2e-player-a', displayName: 'E2E Player A' },
    { accountId: 'e2e-player-b', displayName: 'E2E Player B' },
  ],
} as const;
export const E2E_SONG_ID = 'e2e-song-01';
export const E2E_COMBO_ID = '01+02';

export type ApiOverride = {
  method?: string;
  path: string | RegExp;
  status: number;
  body?: unknown;
  delayMs?: number;
  remaining?: number;
};

export type AppScenario = {
  name: string;
  publication: PublicationResponse;
  features: FeatureFlagsResponse;
  serviceInfo: ServiceInfoResponse;
  songs: SongsResponse;
  songsEtag: string;
  shop: ShopResponse;
  player: PlayerResponse;
  syncStatus: SyncStatusResponse;
  notifications: ImprovementNotificationsEnvelope;
  playerHistory: PlayerHistoryResponse;
  playerStats: PlayerStatsResponse;
  playerBands: PlayerBandsResponse;
  leaderboard: LeaderboardResponse;
  leaderboardOffsets: LeaderboardRankOffsetsResponse;
  allLeaderboards: AllLeaderboardsResponse;
  selectedMemberScores: SelectedMemberSongScoresResponse;
  songBandLeaderboard: SongBandLeaderboardResponse;
  allSongBandLeaderboards: AllSongBandLeaderboardsResponse;
  rankings: RankingsPageResponse;
  playerRanking: AccountRankingDto;
  selectedMemberRankings: SelectedMemberRankingsResponse;
  compositeRankings: CompositePageResponse;
  bandRankings: BandRankingsPageResponse;
  bandRanking: BandRankingDto;
  bandDetail: BandDetailResponse;
  bandSearch: BandSearchResponse;
  bandRankHistory: BandRankHistoryResponse;
  bandSongs: BandSongsResponse;
  bandSongRows: BandSongRowsResponse;
  bandCombos: BandComboCatalogResponse;
  rankHistory: RankHistoryResponse;
  rivalsOverview: RivalsOverviewResponse;
  rivalsList: RivalsListResponse;
  rivalDetail: RivalDetailResponse;
  rivalSuggestions: RivalSuggestionsResponse;
  rivalsAll: RivalsAllResponse;
  leaderboardRivals: LeaderboardRivalsListResponse;
  leaderboardNeighborhood: LeaderboardNeighborhoodResponse;
  overrides: ApiOverride[];
};

export function createEmptyScenario(): AppScenario {
  const populated = createPopulatedScenario();
  return {
    ...populated,
    name: 'empty',
    shop: { songs: [], newSongs: [], lastUpdated: E2E_NOW },
    player: { ...populated.player, totalScores: 0, scores: [] },
    notifications: { ...populated.notifications, items: [] },
    playerHistory: { ...populated.playerHistory, count: 0, history: [] },
    playerStats: {
      ...populated.playerStats,
      totalSongs: 0,
      instruments: [],
      compositeRanks: null,
      familyRanks: null,
      instrumentRanks: [],
      bands: emptyBands(),
    },
    playerBands: emptyBands(),
    leaderboard: { ...populated.leaderboard, count: 0, totalEntries: 0, localEntries: 0, entries: [] },
    allLeaderboards: {
      ...populated.allLeaderboards,
      instruments: populated.allLeaderboards.instruments.map(instrument => ({
        ...instrument,
        count: 0,
        totalEntries: 0,
        localEntries: 0,
        entries: [],
      })),
    },
    selectedMemberScores: { ...populated.selectedMemberScores, scores: [] },
    songBandLeaderboard: {
      ...populated.songBandLeaderboard,
      count: 0,
      totalEntries: 0,
      localEntries: 0,
      entries: [],
      selectedPlayerEntry: null,
      selectedBandEntry: null,
    },
    allSongBandLeaderboards: {
      ...populated.allSongBandLeaderboards,
      bands: populated.allSongBandLeaderboards.bands.map(band => ({
        ...band,
        count: 0,
        totalEntries: 0,
        localEntries: 0,
        entries: [],
        selectedPlayerEntry: null,
        selectedBandEntry: null,
      })),
    },
    rankings: { ...populated.rankings, totalAccounts: 0, entries: [] },
    selectedMemberRankings: {
      ...populated.selectedMemberRankings,
      instruments: populated.selectedMemberRankings.instruments.map(instrument => ({
        ...instrument,
        totalAccounts: 0,
        entries: [],
      })),
    },
    compositeRankings: { ...populated.compositeRankings, totalAccounts: 0, entries: [] },
    bandRankings: {
      ...populated.bandRankings,
      totalTeams: 0,
      entries: [],
      selectedPlayerEntry: null,
      selectedBandEntry: null,
    },
    bandSearch: {
      ...populated.bandSearch,
      totalCount: 0,
      interpretations: [],
      results: [],
    },
    bandSongs: { ...populated.bandSongs, best: [], worst: [] },
    bandSongRows: { ...populated.bandSongRows, count: 0, entries: [] },
    bandCombos: { ...populated.bandCombos, combos: [] },
    rankHistory: { ...populated.rankHistory, history: [] },
    bandRankHistory: { ...populated.bandRankHistory, history: [] },
    rivalsOverview: { ...populated.rivalsOverview, combos: [] },
    rivalsList: { ...populated.rivalsList, above: [], below: [] },
    rivalDetail: { ...populated.rivalDetail, totalSongs: 0, songs: [] },
    rivalSuggestions: { ...populated.rivalSuggestions, rivals: [] },
    rivalsAll: { ...populated.rivalsAll, songs: [], combos: [] },
    leaderboardRivals: { ...populated.leaderboardRivals, userRank: null, above: [], below: [] },
  };
}

export function createScrollableShopScenario(): AppScenario {
  const populated = createPopulatedScenario();
  return {
    ...populated,
    name: 'scrollable-shop',
    shop: {
      songs: populated.songs.songs.map((song, index) => ({
        songId: song.songId,
        title: song.title,
        artist: song.artist,
        year: song.year,
        albumArt: song.albumArt,
        shopUrl: `https://example.invalid/shop/${song.songId}`,
        isNew: index === 0,
        leavingTomorrow: index === populated.songs.songs.length - 1,
      })),
      newSongs: [populated.songs.songs[0]!.songId],
      lastUpdated: E2E_NOW,
    },
  };
}

export function createPopulatedScenario(): AppScenario {
  const songs = createSongs();
  const scores = songs.slice(0, 10).map((song, index) => ({
    songId: song.songId,
    songTitle: song.title,
    songArtist: song.artist,
    instrument: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'][index % 4]!,
    score: index === 0 ? 130_000 : 88_000 + index * 2_100,
    rank: index + 2,
    localRank: index + 2,
    percentile: 98 - index * 2,
    accuracy: 96 - index,
    isFullCombo: index % 3 === 0,
    stars: index % 4 === 0 ? 6 : 5,
    season: 5,
    difficulty: 3 + (index % 4),
    totalEntries: 1_200,
    minLeeway: index === 0 ? 2 : null,
    validScores: index === 0
      ? [{
          score: 120_000,
          accuracy: 98,
          fc: false,
          stars: 6,
          minLeeway: 1,
          rankTiers: [{ leeway: 2, rank: 4 }, { leeway: 5, rank: 3 }],
        }]
      : null,
  })) satisfies PlayerResponse['scores'];

  const rankingEntries = Array.from({ length: 12 }, (_, index) =>
    createAccountRankingEntry(
      index === 2 ? E2E_PLAYER.accountId : `e2e-ranking-${index + 1}`,
      index === 2 ? E2E_PLAYER.displayName : `Ranked Player ${index + 1}`,
      index + 1,
    ));
  const leaderboardEntries = Array.from({ length: 30 }, (_, index) => ({
    accountId: index === 2 ? E2E_PLAYER.accountId : `e2e-score-${index + 1}`,
    displayName: index === 2 ? E2E_PLAYER.displayName : `Score Player ${index + 1}`,
    score: 125_000 - index * 2_500,
    rank: index + 1,
    localRank: index + 1,
    percentile: 100 - index * 3,
    accuracy: 100 - index,
    isFullCombo: index < 2,
    stars: index < 5 ? 6 : 5,
    season: 5,
    difficulty: 5,
  })) satisfies LeaderboardResponse['entries'];

  const bandMembers = [
    { accountId: E2E_BAND.members[0].accountId, displayName: E2E_BAND.members[0].displayName, instruments: ['Solo_Guitar'] },
    { accountId: E2E_BAND.members[1].accountId, displayName: E2E_BAND.members[1].displayName, instruments: ['Solo_Bass'] },
  ] satisfies BandDetailResponse['band']['members'];
  const bandEntries = Array.from({ length: 6 }, (_, index) => ({
    bandId: index === 1 ? E2E_BAND.bandId : `e2e-band-${index + 1}`,
    bandType: 'Band_Duets' as const,
    teamKey: index === 1 ? E2E_BAND.teamKey : `e2e-band-${index + 1}:mate-${index + 1}`,
    comboId: E2E_COMBO_ID,
    members: index === 1
      ? bandMembers
      : [
          { accountId: `e2e-band-${index + 1}`, displayName: `Band Member ${index + 1}A`, instruments: ['Solo_Guitar'] },
          { accountId: `mate-${index + 1}`, displayName: `Band Member ${index + 1}B`, instruments: ['Solo_Bass'] },
        ],
    score: 245_000 - index * 5_000,
    rank: index + 1,
    percentile: 99 - index * 4,
    accuracy: 98 - index,
    isFullCombo: index === 0,
    stars: 6,
    season: 5,
    difficulty: 5,
    endTime: E2E_NOW,
  })) satisfies SongBandLeaderboardResponse['entries'];
  const bandRankingEntries = Array.from({ length: 6 }, (_, index) =>
    createBandRankingEntry(
      index === 1 ? E2E_BAND.bandId : `e2e-band-${index + 1}`,
      index === 1 ? E2E_BAND.teamKey : `e2e-band-${index + 1}:mate-${index + 1}`,
      index + 1,
      index === 1 ? bandMembers : undefined,
    ));

  const playerBands = {
    all: { totalCount: 3, entries: createPlayerBandEntries() },
    duos: { totalCount: 1, entries: createPlayerBandEntries().slice(0, 1) },
    trios: { totalCount: 1, entries: createPlayerBandEntries().slice(1, 2) },
    quads: { totalCount: 1, entries: createPlayerBandEntries().slice(2, 3) },
  } satisfies PlayerBandsResponse;
  const playerRanking = {
    ...rankingEntries[2]!,
    instrument: 'Solo_Guitar',
    totalRankedAccounts: 1_200,
  } satisfies AccountRankingDto;
  const bandRanking = {
    ...bandRankingEntries[1]!,
    bandType: 'Band_Duets',
    comboId: E2E_COMBO_ID,
    totalRankedTeams: 240,
  } satisfies BandRankingDto;

  return {
    name: 'populated',
    publication: {
      contractVersion: 1,
      publicationId: 1,
      previousPublicationId: null,
      publishedScrapeId: 1,
      publishedAt: E2E_NOW,
      readyForPinning: true,
      pinningEnabled: true,
      unreadySurfaces: [],
    },
    features: { appManual: true },
    serviceInfo: createServiceInfo(),
    songs: { count: songs.length, currentSeason: 5, songs },
    songsEtag: '"e2e-songs-v1"',
    shop: {
      songs: [
        {
          songId: songs[0]!.songId,
          title: songs[0]!.title,
          artist: songs[0]!.artist,
          year: songs[0]!.year,
          albumArt: songs[0]!.albumArt,
          shopUrl: 'https://example.invalid/shop/e2e-song-01',
          isNew: true,
        },
        {
          songId: songs[1]!.songId,
          title: songs[1]!.title,
          artist: songs[1]!.artist,
          year: songs[1]!.year,
          albumArt: songs[1]!.albumArt,
          shopUrl: 'https://example.invalid/shop/e2e-song-02',
          leavingTomorrow: true,
        },
      ],
      newSongs: [songs[0]!.songId],
      lastUpdated: E2E_NOW,
    },
    player: {
      accountId: E2E_PLAYER.accountId,
      displayName: E2E_PLAYER.displayName,
      totalScores: scores.length,
      scores,
    },
    syncStatus: {
      accountId: E2E_PLAYER.accountId,
      isTracked: true,
      pendingRankUpdate: false,
      backfill: null,
      historyRecon: null,
      rivals: null,
    },
    notifications: {
      generatedAt: E2E_NOW,
      expiresAfterHours: 72,
      sourceRunId: 1,
      sourceCompletedAt: E2E_NOW,
      notificationsGenerated: true,
      items: [],
    },
    playerHistory: {
      accountId: E2E_PLAYER.accountId,
      count: 8,
      history: Array.from({ length: 8 }, (_, index) => ({
        songId: songs[index]!.songId,
        instrument: 'Solo_Guitar',
        oldScore: 80_000 + index * 1_000,
        newScore: 90_000 + index * 1_500,
        oldRank: 30 - index,
        newRank: 20 - index,
        accuracy: 91 + index,
        isFullCombo: index > 4,
        stars: index > 2 ? 6 : 5,
        percentile: 80 + index,
        season: 5,
        changedAt: new Date(Date.parse(E2E_NOW) - index * 86_400_000).toISOString(),
      })),
    },
    playerStats: createPlayerStats(playerBands),
    playerBands,
    leaderboard: {
      songId: E2E_SONG_ID,
      instrument: 'Solo_Guitar',
      showLeaderboardEntryTotals: true,
      count: leaderboardEntries.length,
      totalEntries: 1_200,
      localEntries: leaderboardEntries.length,
      entries: leaderboardEntries,
    },
    leaderboardOffsets: {
      songId: E2E_SONG_ID,
      instrument: 'Solo_Guitar',
      maxScore: 125_000,
      minLeewayTenths: 0,
      maxLeewayTenths: 50,
      stepTenths: 5,
      removed: [0, 1, 2],
      exact: [true, true, false],
      generatedAt: E2E_NOW,
    },
    allLeaderboards: {
      songId: E2E_SONG_ID,
      showLeaderboardEntryTotals: true,
      instruments: ['Solo_Guitar', 'Solo_Bass'].map((instrument, index) => ({
        instrument,
        count: leaderboardEntries.length,
        totalEntries: 1_200 - index * 100,
        localEntries: leaderboardEntries.length,
        entries: leaderboardEntries,
      })),
    },
    selectedMemberScores: {
      songId: E2E_SONG_ID,
      scores: bandMembers.map((member, index) => ({
        accountId: member.accountId,
        displayName: member.displayName,
        songId: E2E_SONG_ID,
        instrument: member.instruments[0]!,
        score: 110_000 - index * 5_000,
        rank: index + 4,
        accuracy: 96 - index,
        isFullCombo: index === 0,
        stars: 6,
        season: 5,
        totalEntries: 1_200,
      })),
    },
    songBandLeaderboard: {
      songId: E2E_SONG_ID,
      bandType: 'Band_Duets',
      showLeaderboardEntryTotals: true,
      count: bandEntries.length,
      totalEntries: 240,
      localEntries: bandEntries.length,
      entries: bandEntries,
      selectedPlayerEntry: bandEntries[1],
      selectedBandEntry: bandEntries[1],
    },
    allSongBandLeaderboards: {
      songId: E2E_SONG_ID,
      showLeaderboardEntryTotals: true,
      bands: [{
        bandType: 'Band_Duets',
        count: bandEntries.length,
        totalEntries: 240,
        localEntries: bandEntries.length,
        entries: bandEntries,
        selectedPlayerEntry: bandEntries[1],
        selectedBandEntry: bandEntries[1],
      }],
    },
    rankings: {
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      page: 1,
      pageSize: 10,
      totalAccounts: 1_200,
      entries: rankingEntries,
    },
    playerRanking,
    selectedMemberRankings: {
      rankBy: 'totalscore',
      instruments: [{
        instrument: 'Solo_Guitar',
        rankBy: 'totalscore',
        totalAccounts: 1_200,
        entries: [playerRanking],
      }],
    },
    compositeRankings: {
      page: 1,
      pageSize: 10,
      totalAccounts: 1_200,
      entries: rankingEntries.slice(0, 5).map((entry, index) => ({
        accountId: entry.accountId,
        displayName: entry.displayName,
        instrumentsPlayed: 4,
        totalSongsPlayed: entry.songsPlayed,
        compositeRating: 0.9 - index * 0.03,
        compositeRank: index + 1,
        instruments: {
          guitar: { skill: entry.adjustedSkillRating, rank: entry.adjustedSkillRank },
          bass: { skill: entry.adjustedSkillRating - 0.02, rank: entry.adjustedSkillRank + 1 },
          drums: null,
          vocals: null,
          proGuitar: null,
          proBass: null,
        },
        computedAt: E2E_NOW,
      })),
    },
    bandRankings: {
      bandType: 'Band_Duets',
      comboId: E2E_COMBO_ID,
      rankBy: 'adjusted',
      page: 1,
      pageSize: 10,
      totalTeams: 240,
      entries: bandRankingEntries,
      selectedPlayerEntry: bandRankingEntries[1],
      selectedBandEntry: bandRankingEntries[1],
    },
    bandRanking,
    bandDetail: {
      band: {
        bandId: E2E_BAND.bandId,
        teamKey: E2E_BAND.teamKey,
        bandType: 'Band_Duets',
        appearanceCount: 12,
        members: bandMembers,
      },
      ranking: bandRanking,
      configurations: [{
        rawInstrumentCombo: 'Solo_Guitar+Solo_Bass',
        comboId: E2E_COMBO_ID,
        instruments: ['Solo_Guitar', 'Solo_Bass'],
        assignmentKey: `${bandMembers[0]!.accountId}:Solo_Guitar|${bandMembers[1]!.accountId}:Solo_Bass`,
        appearanceCount: 12,
        memberInstruments: {
          [bandMembers[0]!.accountId]: 'Solo_Guitar',
          [bandMembers[1]!.accountId]: 'Solo_Bass',
        },
      }],
    },
    bandSearch: {
      query: E2E_BAND.displayName,
      normalizedQuery: E2E_BAND.displayName.toLowerCase(),
      bandType: 'Band_Duets',
      comboId: E2E_COMBO_ID,
      rankBy: 'adjusted',
      page: 1,
      pageSize: 10,
      totalCount: 1,
      isAmbiguous: false,
      needsDisambiguation: false,
      interpretations: [],
      results: [{
        bandId: E2E_BAND.bandId,
        teamKey: E2E_BAND.teamKey,
        bandType: 'Band_Duets',
        appearanceCount: 12,
        members: bandMembers,
        ranking: bandRanking,
        matchedInterpretationIds: [],
        matchedAccountIds: bandMembers.map(member => member.accountId),
      }],
    },
    bandRankHistory: {
      bandType: 'Band_Duets',
      teamKey: E2E_BAND.teamKey,
      comboId: E2E_COMBO_ID,
      days: 30,
      historyStatus: 'current',
      history: createRankHistoryEntries(240).map((entry, index) => ({
        snapshotDate: entry.snapshotDate,
        snapshotTakenAt: entry.snapshotTakenAt,
        adjustedSkillRank: 11 - index,
        weightedRank: 12 - index,
        fcRateRank: 13 - index,
        totalScoreRank: 14 - index,
        adjustedSkillRating: entry.adjustedSkillRating,
        weightedRating: entry.weightedRating,
        fcRate: entry.fcRate,
        totalScore: entry.totalScore,
        songsPlayed: entry.songsPlayed,
        coverage: entry.coverage,
        fullComboCount: entry.fullComboCount,
        totalChartedSongs: entry.totalChartedSongs,
        totalRankedTeams: entry.rankedAccountCount,
        rawWeightedRating: entry.rawWeightedRating,
        rawSkillRating: entry.rawSkillRating,
      })),
    },
    bandSongs: {
      bandType: 'Band_Duets',
      teamKey: E2E_BAND.teamKey,
      comboId: E2E_COMBO_ID,
      limit: 5,
      best: createBandSongPerformances(songs.slice(0, 5)),
      worst: createBandSongPerformances(songs.slice(5, 10)),
    },
    bandSongRows: {
      bandType: 'Band_Duets',
      teamKey: E2E_BAND.teamKey,
      comboId: E2E_COMBO_ID,
      count: 10,
      entries: createBandSongPerformances(songs.slice(0, 10)),
    },
    bandCombos: {
      bandType: 'Band_Duets',
      combos: [{
        comboId: E2E_COMBO_ID,
        instruments: ['Solo_Guitar', 'Solo_Bass'],
        teamCount: 240,
      }],
    },
    rankHistory: {
      instrument: 'Solo_Guitar',
      accountId: E2E_PLAYER.accountId,
      history: createRankHistoryEntries(1_200),
    },
    rivalsOverview: {
      accountId: E2E_PLAYER.accountId,
      computedAt: E2E_NOW,
      combos: [{ combo: 'Solo_Guitar', aboveCount: 1, belowCount: 1 }],
    },
    rivalsList: {
      combo: 'Solo_Guitar',
      above: [{
        accountId: E2E_RIVAL.accountId,
        displayName: E2E_RIVAL.displayName,
        rivalScore: 0.91,
        sharedSongCount: 10,
        aheadCount: 6,
        behindCount: 4,
        avgSignedDelta: 3.2,
      }],
      below: [{
        accountId: 'e2e-rival-below',
        displayName: 'Chasing Player',
        rivalScore: 0.83,
        sharedSongCount: 8,
        aheadCount: 3,
        behindCount: 5,
        avgSignedDelta: -2.1,
      }],
    },
    rivalDetail: {
      rival: E2E_RIVAL,
      combo: 'Solo_Guitar',
      source: 'precomputed',
      totalSongs: 6,
      offset: 0,
      limit: 0,
      sort: 'closest',
      songs: songs.slice(0, 6).map((song, index) => ({
        songId: song.songId,
        title: song.title,
        artist: song.artist,
        instrument: 'Solo_Guitar',
        userRank: 10 + index,
        rivalRank: 8 + index,
        rankDelta: 2,
        userScore: 100_000 - index * 1_000,
        rivalScore: 102_000 - index * 1_000,
      })),
    },
    rivalSuggestions: {
      accountId: E2E_PLAYER.accountId,
      combo: 'Solo_Guitar',
      computedAt: E2E_NOW,
      rivals: [{
        accountId: E2E_RIVAL.accountId,
        displayName: E2E_RIVAL.displayName,
        direction: 'above',
        sharedSongCount: 6,
        aheadCount: 4,
        behindCount: 2,
        songs: songs.slice(0, 3).map((song, index) => ({
          songId: song.songId,
          instrument: 'Solo_Guitar',
          userRank: 12 + index,
          rivalRank: 10 + index,
          rankDelta: 2,
          userScore: 100_000,
          rivalScore: 102_000,
        })),
      }],
    },
    rivalsAll: {
      accountId: E2E_PLAYER.accountId,
      songs: songs.slice(0, 3).map(song => song.songId),
      combos: [{
        combo: 'Solo_Guitar',
        above: [{
          accountId: E2E_RIVAL.accountId,
          displayName: E2E_RIVAL.displayName,
          direction: 'above',
          sharedSongCount: 3,
          aheadCount: 2,
          behindCount: 1,
          rivalScore: 0.91,
          samples: songs.slice(0, 3).map((_, index) => ({
            s: index,
            i: 'Solo_Guitar',
            ur: 12 + index,
            rr: 10 + index,
            us: 100_000,
            rs: 102_000,
          })),
        }],
        below: [],
      }],
    },
    leaderboardRivals: {
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      userRank: 12,
      above: [{
        accountId: E2E_RIVAL.accountId,
        displayName: E2E_RIVAL.displayName,
        sharedSongCount: 10,
        aheadCount: 6,
        behindCount: 4,
        avgSignedDelta: 3.2,
        leaderboardRank: 11,
        userLeaderboardRank: 12,
      }],
      below: [],
    },
    leaderboardNeighborhood: {
      instrument: 'Solo_Guitar',
      accountId: E2E_PLAYER.accountId,
      rank: 12,
      above: [{
        accountId: E2E_RIVAL.accountId,
        displayName: E2E_RIVAL.displayName,
        totalScore: 1_050_000,
        totalScoreRank: 11,
        songsPlayed: 10,
        totalChartedSongs: 12,
        coverage: 0.83,
        adjustedSkillRating: 0.91,
        adjustedSkillRank: 11,
      }],
      self: {
        accountId: E2E_PLAYER.accountId,
        displayName: E2E_PLAYER.displayName,
        totalScore: 1_000_000,
        totalScoreRank: 12,
        songsPlayed: 10,
        totalChartedSongs: 12,
        coverage: 0.83,
        adjustedSkillRating: 0.89,
        adjustedSkillRank: 12,
      },
      below: [],
    },
    overrides: [],
  };
}

function createSongs(): SongsResponse['songs'] {
  return Array.from({ length: 12 }, (_, index) => ({
    songId: `e2e-song-${String(index + 1).padStart(2, '0')}`,
    title: index === 0
      ? 'A Very Long Deterministic Festival Song Title For Responsive Testing'
      : `Deterministic Song ${index + 1}`,
    artist: index === 1
      ? 'An Artist With A Deliberately Long Display Name'
      : `Festival Artist ${index + 1}`,
    album: `Album ${index + 1}`,
    year: 2015 + index,
    tempo: 90 + index * 5,
    durationSeconds: 160 + index * 3,
    albumArt: '/icons/fst-icon.svg',
    genres: ['Rock'],
    difficulty: {
      guitar: 1 + (index % 7),
      bass: 1 + ((index + 1) % 7),
      drums: 1 + ((index + 2) % 7),
      vocals: 1 + ((index + 3) % 7),
      proGuitar: index % 2 === 0 ? 4 : 0,
      proBass: index % 2 === 0 ? 3 : 0,
    },
    maxScores: {
      Solo_Guitar: 125_000,
      Solo_Bass: 115_000,
      Solo_Drums: 135_000,
      Solo_Vocals: 105_000,
    },
    pathsGeneratedAt: index === 0 ? E2E_NOW : undefined,
    pathArtifactGenerationId: index === 0 ? 'e2e-path-generation' : undefined,
    pathExpectedInstruments: index === 0 ? ['Solo_Guitar'] : undefined,
  }));
}

function createServiceInfo(): ServiceInfoResponse {
  return {
    lastCompletedUpdate: {
      scrapeId: 1,
      startedAt: E2E_NOW,
      completedAt: E2E_NOW,
      publishedAt: E2E_NOW,
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
      publishedAt: E2E_NOW,
      publicReadsFrozen: false,
      frozenAt: null,
      frozenScrapeId: null,
      freezeReason: null,
    },
    workerStatus: {
      workerKey: 'e2e-worker',
      status: 'online',
      rawStatus: 'idle',
    },
    nextScheduledUpdateAt: '2026-01-02T12:00:00.000Z',
  };
}

function createPlayerStats(bands: PlayerBandsResponse): PlayerStatsResponse {
  return {
    accountId: E2E_PLAYER.accountId,
    totalSongs: 12,
    instruments: [{
      instrument: 'Solo_Guitar',
      tiers: [{
        minLeeway: null,
        songsPlayed: 10,
        overThresholdCount: 1,
        fcCount: 4,
        fcPercent: 40,
        goldStarCount: 5,
        fiveStarCount: 4,
        fourStarCount: 1,
        threeStarCount: 0,
        twoStarCount: 0,
        oneStarCount: 0,
        avgAccuracy: 95,
        bestAccuracy: 100,
        averageStars: 5.3,
        avgScore: 100_000,
        totalScore: 1_000_000,
        completionPercent: 83,
        bestRank: 2,
        bestRankSongId: E2E_SONG_ID,
        percentileDist: JSON.stringify({ top1: 1, top5: 3, top10: 4 }),
        avgPercentile: '7.2',
        overallPercentile: '5.8',
        topSongs: JSON.stringify([E2E_SONG_ID]),
        bottomSongs: JSON.stringify(['e2e-song-10']),
        bestRankInstrument: 'Solo_Guitar',
      }],
    }],
    compositeRanks: { adjusted: 12, weighted: 14, fcRate: 18, totalScore: 9, maxScore: 11 },
    familyRanks: null,
    instrumentRanks: [],
    bands,
  };
}

function createAccountRankingEntry(
  accountId: string,
  displayName: string,
  rank: number,
): AccountRankingEntry {
  return {
    accountId,
    displayName,
    songsPlayed: 10,
    totalChartedSongs: 12,
    coverage: 0.83,
    rawSkillRating: 0.95 - rank * 0.01,
    adjustedSkillRating: 0.94 - rank * 0.01,
    adjustedSkillRank: rank,
    weightedRating: 0.9 - rank * 0.01,
    weightedRank: rank + 1,
    fcRate: 0.5,
    fcRateRank: rank + 2,
    totalScore: 1_200_000 - rank * 10_000,
    totalScoreRank: rank,
    maxScorePercent: 0.98 - rank * 0.005,
    maxScorePercentRank: rank + 1,
    avgAccuracy: 950_000 - rank * 1_000,
    fullComboCount: 4,
    avgStars: 5.4,
    bestRank: 1,
    avgRank: rank + 2,
    rawMaxScorePercent: 0.98 - rank * 0.005,
    rawWeightedRating: 0.9 - rank * 0.01,
    computedAt: E2E_NOW,
  };
}

function createBandRankingEntry(
  bandId: string,
  teamKey: string,
  rank: number,
  members?: BandRankingsPageResponse['entries'][number]['members'],
): BandRankingsPageResponse['entries'][number] {
  const teamMembers = members?.map(member => ({
    accountId: member.accountId,
    displayName: member.displayName,
  })) ?? [
    { accountId: `${bandId}-a`, displayName: `Band ${rank} A` },
    { accountId: `${bandId}-b`, displayName: `Band ${rank} B` },
  ];
  return {
    bandId,
    comboId: E2E_COMBO_ID,
    teamKey,
    teamMembers,
    members,
    configurations: [],
    songsPlayed: 10,
    totalChartedSongs: 12,
    coverage: 0.83,
    rawSkillRating: 0.9 - rank * 0.01,
    adjustedSkillRating: 0.88 - rank * 0.01,
    adjustedSkillRank: rank,
    weightedRating: 0.84 - rank * 0.01,
    weightedRank: rank + 1,
    fcRate: 0.5,
    fcRateRank: rank + 2,
    totalScore: 2_000_000 - rank * 20_000,
    totalScoreRank: rank,
    avgAccuracy: 960_000 - rank * 1_000,
    fullComboCount: 4,
    avgStars: 5.5,
    bestRank: 1,
    avgRank: rank + 1,
    rawWeightedRating: 0.84 - rank * 0.01,
    computedAt: E2E_NOW,
  };
}

function createPlayerBandEntries(): PlayerBandsResponse['all']['entries'] {
  return [
    {
      bandId: E2E_BAND.bandId,
      teamKey: E2E_BAND.teamKey,
      bandType: 'Band_Duets',
      appearanceCount: 12,
      members: [
        { accountId: E2E_BAND.members[0].accountId, displayName: E2E_BAND.members[0].displayName, instruments: ['Solo_Guitar'] },
        { accountId: E2E_BAND.members[1].accountId, displayName: E2E_BAND.members[1].displayName, instruments: ['Solo_Bass'] },
      ],
    },
    {
      bandId: 'e2e-trio',
      teamKey: 'e2e-player-a:e2e-player-b:e2e-player-c',
      bandType: 'Band_Trios',
      appearanceCount: 8,
      members: [
        { accountId: 'e2e-player-a', displayName: 'E2E Player A', instruments: ['Solo_Guitar'] },
        { accountId: 'e2e-player-b', displayName: 'E2E Player B', instruments: ['Solo_Bass'] },
        { accountId: 'e2e-player-c', displayName: 'E2E Player C', instruments: ['Solo_Drums'] },
      ],
    },
    {
      bandId: 'e2e-quad',
      teamKey: 'e2e-player-a:e2e-player-b:e2e-player-c:e2e-player-d',
      bandType: 'Band_Quad',
      appearanceCount: 5,
      members: [
        { accountId: 'e2e-player-a', displayName: 'E2E Player A', instruments: ['Solo_Guitar'] },
        { accountId: 'e2e-player-b', displayName: 'E2E Player B', instruments: ['Solo_Bass'] },
        { accountId: 'e2e-player-c', displayName: 'E2E Player C', instruments: ['Solo_Drums'] },
        { accountId: 'e2e-player-d', displayName: 'E2E Player D', instruments: ['Solo_Vocals'] },
      ],
    },
  ];
}

function emptyBands(): PlayerBandsResponse {
  return {
    all: { totalCount: 0, entries: [] },
    duos: { totalCount: 0, entries: [] },
    trios: { totalCount: 0, entries: [] },
    quads: { totalCount: 0, entries: [] },
  };
}

function createRankHistoryEntries(totalRanked: number): RankHistoryResponse['history'] {
  return Array.from({ length: 5 }, (_, index) => ({
    snapshotDate: `2025-12-${String(28 + index).padStart(2, '0')}`,
    snapshotTakenAt: new Date(Date.parse(E2E_NOW) - (4 - index) * 86_400_000).toISOString(),
    adjustedSkillRank: 18 - index,
    weightedRank: 20 - index,
    fcRateRank: 24 - index,
    totalScoreRank: 15 - index,
    maxScorePercentRank: 17 - index,
    adjustedSkillRating: 0.82 + index * 0.01,
    weightedRating: 0.78 + index * 0.01,
    fcRate: 0.4 + index * 0.02,
    totalScore: 900_000 + index * 25_000,
    maxScorePercent: 0.9 + index * 0.01,
    songsPlayed: 8 + index,
    coverage: 0.66 + index * 0.04,
    fullComboCount: 2 + index,
    totalChartedSongs: 12,
    rankedAccountCount: totalRanked,
    rawMaxScorePercent: 0.9 + index * 0.01,
    rawWeightedRating: 0.78 + index * 0.01,
    rawSkillRating: 0.83 + index * 0.01,
  }));
}

function createBandSongPerformances(
  songs: SongsResponse['songs'],
): BandSongRowsResponse['entries'] {
  return songs.map((song, index) => ({
    songId: song.songId,
    comboId: E2E_COMBO_ID,
    rank: index + 1,
    totalEntries: 240,
    percentile: 99 - index * 5,
    score: 240_000 - index * 4_000,
    accuracy: 98 - index,
    isFullCombo: index === 0,
    stars: 6,
    season: 5,
    endTime: E2E_NOW,
  }));
}
