/**
 * Typed query key factory for React Query.
 * Every query key in the app should be produced by one of these functions
 * to enable targeted invalidation and avoid stale-key bugs.
 */
export const queryKeys = {
  songs: () => ['songs'] as const,
  player: (accountId: string, songId?: string, instruments?: string[]) =>
    ['player', accountId, { songId, instruments }] as const,
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
};
