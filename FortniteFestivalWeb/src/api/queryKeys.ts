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
  playerStats: (accountId: string) => ['playerStats', accountId] as const,
  version: () => ['version'] as const,
  rivalsOverview: (accountId: string) => ['rivalsOverview', accountId] as const,
  rivalsList: (accountId: string, combo: string) => ['rivalsList', accountId, combo] as const,
  rivalDetail: (accountId: string, combo: string, rivalId: string) =>
    ['rivalDetail', accountId, combo, rivalId] as const,
  rankings: (instrument: string, rankBy?: string, page?: number, pageSize?: number, leeway?: number | null) =>
    ['rankings', instrument, { rankBy, page, pageSize, leeway }] as const,
  playerRanking: (instrument: string, accountId: string, leeway?: number | null, rankBy?: string) =>
    ['playerRanking', instrument, accountId, { leeway, rankBy }] as const,
  compositeRankings: (page?: number, pageSize?: number) =>
    ['compositeRankings', { page, pageSize }] as const,
  playerCompositeRanking: (accountId: string) =>
    ['playerCompositeRanking', accountId] as const,
  comboRankings: (comboId: string, rankBy?: string, page?: number, pageSize?: number) =>
    ['comboRankings', comboId, { rankBy, page, pageSize }] as const,
  playerComboRanking: (accountId: string, comboId: string, rankBy?: string) =>
    ['playerComboRanking', accountId, comboId, { rankBy }] as const,
  leaderboardNeighborhood: (instrument: string, accountId: string) =>
    ['leaderboardNeighborhood', instrument, accountId] as const,
  compositeNeighborhood: (accountId: string) =>
    ['compositeNeighborhood', accountId] as const,
  rankHistory: (instrument: string, accountId: string, days?: number) =>
    ['rankHistory', instrument, accountId, { days }] as const,
};
