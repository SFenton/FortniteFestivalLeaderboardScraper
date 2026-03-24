/**
 * Types matching FSTService HTTP API responses.
 * These mirror the DTOs produced by FSTService's ApiEndpoints.
 */

/** Instrument keys as used in the FSTService API (matches C# InstrumentType mapping). */
export type ServerInstrumentKey =
  | 'Solo_Guitar'
  | 'Solo_Bass'
  | 'Solo_Drums'
  | 'Solo_Vocals'
  | 'Solo_PeripheralGuitar'
  | 'Solo_PeripheralBass';

export const SERVER_INSTRUMENT_KEYS: ServerInstrumentKey[] = [
  'Solo_Guitar',
  'Solo_Bass',
  'Solo_Drums',
  'Solo_Vocals',
  'Solo_PeripheralGuitar',
  'Solo_PeripheralBass',
];

export const SERVER_INSTRUMENT_LABELS: Record<ServerInstrumentKey, string> = {
  Solo_Guitar: 'Lead',
  Solo_Bass: 'Bass',
  Solo_Drums: 'Drums',
  Solo_Vocals: 'Vocals',
  Solo_PeripheralGuitar: 'Pro Lead',
  Solo_PeripheralBass: 'Pro Bass',
};

/** Look up the display label for a server instrument key. */
export function serverInstrumentLabel(key: ServerInstrumentKey): string {
  return SERVER_INSTRUMENT_LABELS[key] ?? key;
}

// Convenience aliases used by web layer
export const INSTRUMENT_KEYS = SERVER_INSTRUMENT_KEYS;
export const INSTRUMENT_LABELS = SERVER_INSTRUMENT_LABELS;

/** The preferred default instrument when none is specified. */
export const DEFAULT_INSTRUMENT: ServerInstrumentKey = 'Solo_Guitar';

export type SongDifficulty = {
  guitar?: number;
  bass?: number;
  drums?: number;
  vocals?: number;
  proGuitar?: number;
  proBass?: number;
};

/** Song as returned by the FSTService /api/songs endpoint. */
export type ServerSong = {
  songId: string;
  title: string;
  artist: string;
  album?: string;
  year?: number;
  tempo?: number;
  albumArt?: string;
  genres?: string[];
  difficulty?: SongDifficulty;
  maxScores?: Partial<Record<ServerInstrumentKey, number>>;
  shopUrl?: string;
};

/** Minimal song shape for display purposes (album art required). */
export type SongDisplay = Pick<ServerSong, 'title' | 'artist' | 'year'> & { albumArt: string };

export type SongsResponse = {
  count: number;
  currentSeason?: number;
  songs: ServerSong[];
};

// ─── WebSocket notification types ──────────────────────────────

export type ShopChangedMessage = {
  type: 'shop_changed';
  added: string[];
  removed: string[];
  total: number;
};

export type ShopSnapshotMessage = {
  type: 'shop_snapshot';
  songIds: string[];
  total: number;
};

export type WsNotificationMessage =
  | ShopChangedMessage
  | ShopSnapshotMessage
  | { type: 'personal_db_ready' }
  | { type: 'backfill_complete' }
  | { type: 'history_recon_complete' }
  | { type: 'rivals_complete' };

export type LeaderboardEntry = {
  accountId: string;
  displayName?: string;
  score: number;
  rank: number;
  percentile?: number;
  accuracy?: number;
  isFullCombo?: boolean;
  stars?: number;
  season?: number;
};

export type LeaderboardResponse = {
  songId: string;
  instrument: string;
  count: number;
  totalEntries: number;
  localEntries: number;
  entries: LeaderboardEntry[];
};

export type PlayerScore = {
  songId: string;
  songTitle?: string;
  songArtist?: string;
  instrument: string;
  score: number;
  rank: number;
  percentile?: number;
  accuracy?: number;
  isFullCombo?: boolean;
  stars?: number;
  season?: number;
  endTime?: string;
  totalEntries?: number;
};

export type PlayerResponse = {
  accountId: string;
  displayName: string;
  totalScores: number;
  scores: PlayerScore[];
};

export type AccountCheckResponse = {
  exists: boolean;
  accountId: string | null;
  displayName: string | null;
};

export type AccountSearchResult = {
  accountId: string;
  displayName: string;
};

export type AccountSearchResponse = {
  results: AccountSearchResult[];
};

export type ScrapeProgress = {
  isRunning: boolean;
  phase?: string;
  current?: number;
  total?: number;
  percent?: number;
};

export type FirstSeenEntry = {
  songId: string;
  firstSeenSeason: number;
  estimatedSeason?: boolean;
};

export type FirstSeenResponse = {
  count: number;
  songs: FirstSeenEntry[];
};

export type LeaderboardPopulationEntry = {
  songId: string;
  instrument: string;
  totalEntries: number;
};

export type TrackPlayerResponse = {
  accountId: string;
  displayName: string;
  trackingStarted: boolean;
  backfillStatus: string;
};

export type SyncStatusResponse = {
  accountId: string;
  isTracked: boolean;
  backfill: {
    status: string;
    songsChecked: number;
    totalSongsToCheck: number;
    entriesFound: number;
    startedAt: string | null;
    completedAt: string | null;
  } | null;
  historyRecon: {
    status: string;
    songsProcessed: number;
    totalSongsToProcess: number;
    seasonsQueried: number;
    historyEntriesFound: number;
    startedAt: string | null;
    completedAt: string | null;
  } | null;
};

/** Score history entry as returned by /api/player/{id}/history. */
export type ServerScoreHistoryEntry = {
  songId: string;
  instrument: string;
  oldScore?: number;
  newScore: number;
  oldRank?: number;
  newRank: number;
  accuracy?: number;
  isFullCombo?: boolean;
  stars?: number;
  percentile?: number;
  season?: number;
  scoreAchievedAt?: string;
  seasonRank?: number;
  allTimeRank?: number;
  changedAt: string;
};

export type PlayerHistoryResponse = {
  accountId: string;
  count: number;
  history: ServerScoreHistoryEntry[];
};

/** Leaderboard data for all instruments on one song. */
export type AllLeaderboardsResponse = {
  songId: string;
  instruments: {
    instrument: string;
    count: number;
    totalEntries: number;
    localEntries: number;
    entries: LeaderboardEntry[];
  }[];
};

/** Pre-computed player stats for one instrument (or "Overall"). */
export type PlayerStatEntry = {
  instrument: string;
  songsPlayed: number;
  fullComboCount: number;
  goldStarCount: number;
  avgAccuracy: number;
  bestRank: number;
  bestRankSongId?: string;
  totalScore: number;
  percentileDist?: string;
  avgPercentile?: string;
  overallPercentile?: string;
};

export type PlayerStatsResponse = {
  accountId: string;
  stats: PlayerStatEntry[];
};

// ─── Rivals types ──────────────────────────────────────────────

export type RivalComboSummary = {
  combo: string;
  aboveCount: number;
  belowCount: number;
};

export type RivalsOverviewResponse = {
  accountId: string;
  computedAt: string | null;
  combos: RivalComboSummary[];
};

export type RivalSummary = {
  accountId: string;
  displayName: string | null;
  rivalScore: number;
  sharedSongCount: number;
  aheadCount: number;
  behindCount: number;
  avgSignedDelta: number;
};

export type RivalsListResponse = {
  combo: string;
  above: RivalSummary[];
  below: RivalSummary[];
};

export type RivalSongComparison = {
  songId: string;
  title: string | null;
  artist: string | null;
  instrument: string;
  userRank: number;
  rivalRank: number;
  rankDelta: number;
  userScore: number | null;
  rivalScore: number | null;
};

export type RivalDetailResponse = {
  rival: { accountId: string; displayName: string | null };
  combo: string;
  totalSongs: number;
  offset: number;
  limit: number;
  sort: string;
  songs: RivalSongComparison[];
};
