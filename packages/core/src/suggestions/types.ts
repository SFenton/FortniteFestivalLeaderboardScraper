import type {InstrumentKey} from '../instruments';

// ─── Rival data types ──────────────────────────────────────────

/** Discriminator for which rival system produced the data. */
export type RivalSource = 'song' | 'leaderboard';

/** Summary info about a single rival player. */
export type RivalInfo = {
  accountId: string;
  displayName: string;
  direction: 'above' | 'below';
  source: RivalSource;
  /** Song rivals only: number of per-song overlaps. */
  sharedSongCount?: number;
  /** Song rivals only: songs where this rival is ahead. */
  aheadCount?: number;
  /** Song rivals only: songs where user is ahead. */
  behindCount?: number;
  /** Leaderboard rivals only: per-instrument total score. */
  totalScore?: number;
  /** Leaderboard rivals only: per-instrument total score rank. */
  totalScoreRank?: number;
  /** Leaderboard rivals only: composite rating. */
  compositeRating?: number;
  /** Leaderboard rivals only: composite rank. */
  compositeRank?: number;
};

/** A per-song match between the user and a rival. */
export type RivalSongMatch = {
  rival: RivalInfo;
  songId: string;
  instrument: InstrumentKey;
  userRank: number;
  rivalRank: number;
  /** userRank - rivalRank. Negative = rival ahead. */
  rankDelta: number;
  userScore: number | null;
  rivalScore: number | null;
};

/** Indexed lookup structure for O(1) rival queries. */
export type RivalDataIndex = {
  /** Rivals from per-song neighborhood computation. */
  songRivals: RivalInfo[];
  /** Rivals from global ranking neighborhoods. */
  leaderboardRivals: RivalInfo[];
  /** Union, deduplicated by accountId (prefers song rival when both). */
  allRivals: RivalInfo[];
  /** All song matches per rival: rivalAccountId → RivalSongMatch[]. */
  byRival: Map<string, RivalSongMatch[]>;
  /** Closest rival match per song+instrument: `${songId}:${instrument}` → RivalSongMatch. */
  closestRivalBySong: Map<string, RivalSongMatch>;
  /** Quick leaderboard rival lookup: accountId → RivalInfo. */
  leaderboardRivalIndex: Map<string, RivalInfo>;
};

// ─── Suggestion types ──────────────────────────────────────────

export type SuggestionSongItem = {
  songId: string;
  title: string;
  artist: string;
  year?: number;
  stars?: number;
  percent?: number;
  fullCombo?: boolean;
  instrumentKey?: InstrumentKey;
  percentileDisplay?: string;
  /** Display name of the closest rival on this song (from cross-pollination). */
  rivalName?: string;
  /** Account ID for navigation to rival detail. */
  rivalAccountId?: string;
  /** Signed rank delta vs the rival (negative = they lead). */
  rivalRankDelta?: number;
  /** Which rival system this annotation came from. */
  rivalSource?: RivalSource;
};

export type SuggestionCategory = {
  key: string;
  title: string;
  description: string;
  songs: SuggestionSongItem[];
};
