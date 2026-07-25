/** Navigation-only state retained across route unmounts. */

/* ── SongDetailPage cache ── */

export type SongDetailCache = {
  scrollTop: number;
};
export const songDetailCache = new Map<string, SongDetailCache>();

export function clearSongDetailCache() {
  songDetailCache.clear();
}

/* ── LeaderboardPage cache ── */

export type LeaderboardCache = {
  page: number;
  scrollTop: number;
};
export const leaderboardCache = new Map<string, LeaderboardCache>();

export function clearLeaderboardCache() {
  leaderboardCache.clear();
}

/* ── PlayerPage animation flags ── */

/** Clears the player page render-tracking flags so animations replay. */
export function clearPlayerPageCache() {
  // Animation flags live in PlayerPage.tsx module scope.
  // This is a no-op at the cache level; the page module handles its own flags.
  // Kept for backward compat with App.tsx cache-clearing logic.
}

/* ── RankingsPage cache ── */

export type RankingsCache = {
  page: number;
  scrollTop: number;
};
export const rankingsCache = new Map<string, RankingsCache>();

export function clearRankingsCache() {
  rankingsCache.clear();
}
