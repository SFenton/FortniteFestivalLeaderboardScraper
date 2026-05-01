/**
 * Typed query key factory for React Query.
 * Every query key in the app should be produced by one of these functions
 * to enable targeted invalidation and avoid stale-key bugs.
 */
export const queryKeys = {
  songs: () => ['songs'] as const,
  player: (accountId: string, songId?: string, instruments?: string[], leeway?: number) =>
    ['player', accountId, { songId, instruments, leeway }] as const,
  playerHistory: (accountId: string, songId?: string, instrument?: string) =>
    ['playerHistory', accountId, { songId, instrument }] as const,
  syncStatus: (accountId: string) => ['syncStatus', accountId] as const,
  leaderboard: (songId: string, instrument: string, top: number, offset: number, leeway?: number) =>
    ['leaderboard', songId, instrument, { top, offset, leeway }] as const,
  allLeaderboards: (songId: string, top: number, leeway?: number) =>
    ['allLeaderboards', songId, { top, leeway }] as const,
  songBandLeaderboard: (songId: string, bandType: string, top: number, offset: number, selectedAccountId?: string, selectedTeamKey?: string, comboId?: string) =>
    ['songBandLeaderboard', songId, bandType, { top, offset, selectedAccountId, selectedTeamKey, comboId }] as const,
  allSongBandLeaderboards: (songId: string, top: number, selectedAccountId?: string, selectedBandType?: string, selectedTeamKey?: string, comboId?: string) =>
    ['allSongBandLeaderboards', songId, { top, selectedAccountId, selectedBandType, selectedTeamKey, comboId }] as const,
  playerStats: (accountId: string) => ['playerStats', accountId] as const,
  version: () => ['version'] as const,
  rivalsOverview: (accountId: string) => ['rivalsOverview', accountId] as const,
  rivalsList: (accountId: string, combo: string) => ['rivalsList', accountId, combo] as const,
  rivalDetail: (accountId: string, combo: string, rivalId: string) =>
    ['rivalDetail', accountId, combo, rivalId] as const,
  rankings: (instrument: string, rankBy?: string, page?: number, pageSize?: number) =>
    ['rankings', instrument, { rankBy, page, pageSize }] as const,
  playerRanking: (instrument: string, accountId: string, rankBy?: string) =>
    ['playerRanking', instrument, accountId, { rankBy }] as const,
  compositeRankings: (page?: number, pageSize?: number) =>
    ['compositeRankings', { page, pageSize }] as const,
  playerCompositeRanking: (accountId: string) =>
    ['playerCompositeRanking', accountId] as const,
  comboRankings: (comboId: string, rankBy?: string, page?: number, pageSize?: number) =>
    ['comboRankings', comboId, { rankBy, page, pageSize }] as const,
  playerComboRanking: (accountId: string, comboId: string, rankBy?: string) =>
    ['playerComboRanking', accountId, comboId, { rankBy }] as const,
  bandRankingCombos: (bandType: string) => ['bandRankingCombos', bandType] as const,
  bandRankings: (bandType: string, comboId?: string, rankBy?: string, page?: number, pageSize?: number, selectedAccountId?: string, selectedTeamKey?: string) =>
    ['bandRankings', bandType, { comboId, rankBy, page, pageSize, selectedAccountId, selectedTeamKey }] as const,
  bandRanking: (bandType: string, teamKey: string, comboId?: string, rankBy?: string) =>
    ['bandRanking', bandType, teamKey, { comboId, rankBy }] as const,
  bandRankHistory: (bandType: string, teamKey: string, days?: number, comboId?: string) =>
    ['bandRankHistory', bandType, teamKey, { days, comboId }] as const,
  bandSongs: (bandType: string, teamKey: string, limit?: number, comboId?: string) =>
    ['bandSongs', bandType, teamKey, { limit, comboId }] as const,
  bandDetail: (bandId: string) => ['bandDetail', bandId] as const,
  bandLookup: (accountId: string, bandType: string, teamKey: string) =>
    ['bandLookup', accountId, bandType, teamKey] as const,
  playerBandsList: (accountId: string, group: string, page?: number, pageSize?: number) =>
    ['playerBandsList', accountId, { group, page, pageSize }] as const,
  leaderboardNeighborhood: (instrument: string, accountId: string) =>
    ['leaderboardNeighborhood', instrument, accountId] as const,
  compositeNeighborhood: (accountId: string) =>
    ['compositeNeighborhood', accountId] as const,
  rankHistory: (instrument: string, accountId: string, days?: number) =>
    ['rankHistory', instrument, accountId, { days }] as const,
};
