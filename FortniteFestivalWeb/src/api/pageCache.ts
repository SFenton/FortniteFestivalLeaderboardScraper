/**
 * Page-level UI state caches (scroll positions, animation data).
 * These are NOT data caches — React Query handles API data caching.
 * They exist so that returning to a page via back-navigation can
 * restore scroll position and skip stagger animations.
 */

import type { ServerInstrumentKey as InstrumentKey, LeaderboardEntry, ServerScoreHistoryEntry as ScoreHistoryEntry } from '@festival/core/api/serverTypes';

/* ── SongDetailPage cache ── */

export type InstrumentData = {
  entries: LeaderboardEntry[];
  loading: boolean;
  error: string | null;
};

export type SongDetailCache = {
  instrumentData: Record<InstrumentKey, InstrumentData>;
  scoreHistory: ScoreHistoryEntry[];
  accountId: string | undefined;
  scrollTop: number;
};
export const songDetailCache = new Map<string, SongDetailCache>();

export function clearSongDetailCache() {
  songDetailCache.clear();
}

/* ── LeaderboardPage cache ── */

export type LeaderboardCache = {
  entries: LeaderboardEntry[];
  totalEntries: number;
  localEntries: number;
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
