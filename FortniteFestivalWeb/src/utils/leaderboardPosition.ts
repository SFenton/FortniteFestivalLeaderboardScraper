/** Rows per page on the full song leaderboards (solo instrument and band). */
export const SONG_LEADERBOARD_PAGE_SIZE = 25;

/**
 * 1-based full-leaderboard page that contains `rank`, or `null` when the rank
 * is unknown. Used by every "jump to my position" affordance so solo and band
 * rows land on the same page the full leaderboard would show.
 */
export function getLeaderboardPageForRank(
  rank: number | null | undefined,
  pageSize = SONG_LEADERBOARD_PAGE_SIZE,
): number | null {
  if (rank == null || !Number.isFinite(rank) || rank < 1 || pageSize < 1) return null;
  return Math.floor((rank - 1) / pageSize) + 1;
}
