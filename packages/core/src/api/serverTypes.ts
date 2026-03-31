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
  /** Population tiers per instrument for client-side filtered-total computation. */
  populationTiers?: Partial<Record<ServerInstrumentKey, PopulationTierData>> | null;
  shopUrl?: string;
  leavingTomorrow?: boolean;
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
  leavingTomorrow: string[];
};

export type ShopSnapshotMessage = {
  type: 'shop_snapshot';
  songIds: string[];
  total: number;
  leavingTomorrow: string[];
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
  difficulty?: number;
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
  difficulty?: number;
  endTime?: string;
  totalEntries?: number;
  /** Minimum leeway (%) at which this score is considered valid. Null if no max-score data. */
  minLeeway?: number | null;
  /** Historical valid scores sorted by score DESC, each with its own minLeeway + rankTiers. */
  validScores?: ValidScoreVariant[] | null;
  // Legacy fields (kept for backward compat with live-fallback path)
  isValid?: boolean | null;
  validScore?: number | null;
  validRank?: number | null;
  validAccuracy?: number | null;
  validIsFullCombo?: boolean | null;
  validStars?: number | null;
  validTotalEntries?: number | null;
};

/** A historical valid score with metadata for client-side leeway filtering. */
export type ValidScoreVariant = {
  score: number;
  accuracy?: number | null;
  fc?: boolean | null;
  stars?: number | null;
  /** Minimum leeway (%) at which this fallback score is considered valid. */
  minLeeway: number;
  /** Rank changepoints: at each leeway threshold, what rank this score would have. */
  rankTiers?: RankTier[] | null;
};

/** A single changepoint in a fallback score's rank curve. */
export type RankTier = {
  leeway: number;
  rank: number;
};

/** Population tier data for a single (songId, instrument) pair. */
export type PopulationTierData = {
  /** Count of entries always below the threshold band (score ≤ 0.95 × maxScore). */
  baseCount: number;
  /** Changepoints where the filtered total increments as leeway increases. */
  tiers: PopulationTier[];
};

/** A single changepoint in the population tier curve. */
export type PopulationTier = {
  leeway: number;
  total: number;
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
  difficulty?: number;
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

// ─── Leaderboard Rivals types ──────────────────────────────────

/** A leaderboard rival — extends RivalSummary with rank context. */
export type LeaderboardRivalSummary = {
  accountId: string;
  displayName: string | null;
  sharedSongCount: number;
  aheadCount: number;
  behindCount: number;
  avgSignedDelta: number;
  /** The rival's rank on this instrument/method. */
  leaderboardRank: number;
  /** The logged-in user's rank for context. */
  userLeaderboardRank: number;
};

/** Response from GET /api/player/{accountId}/leaderboard-rivals/{instrument}. */
export type LeaderboardRivalsListResponse = {
  instrument: string;
  rankBy: string;
  userRank: number | null;
  above: LeaderboardRivalSummary[];
  below: LeaderboardRivalSummary[];
};

// ─── Rankings types ────────────────────────────────────────────

/** Ranking metric used for sorting. */
export type RankingMetric = 'adjusted' | 'weighted' | 'fcrate' | 'totalscore' | 'maxscore';

/** Per-instrument ranking entry as returned by /api/rankings/{instrument}. */
export type AccountRankingEntry = {
  accountId: string;
  displayName?: string;
  songsPlayed: number;
  totalChartedSongs: number;
  coverage: number;
  rawSkillRating: number;
  adjustedSkillRating: number;
  adjustedSkillRank: number;
  weightedRating: number;
  weightedRank: number;
  fcRate: number;
  fcRateRank: number;
  totalScore: number;
  totalScoreRank: number;
  maxScorePercent: number;
  maxScorePercentRank: number;
  avgAccuracy: number;
  fullComboCount: number;
  avgStars: number;
  bestRank: number;
  avgRank: number;
  computedAt: string;
};

/** Paginated per-instrument rankings response. */
export type RankingsPageResponse = {
  instrument: string;
  rankBy: string;
  page: number;
  pageSize: number;
  totalAccounts: number;
  entries: AccountRankingEntry[];
};

/** Single account per-instrument ranking (includes totalRankedAccounts). */
export type AccountRankingDto = AccountRankingEntry & {
  instrument: string;
  totalRankedAccounts: number;
};

/** Instrument skill/rank pair used in composite rankings. */
export type InstrumentSkillRank = {
  skill: number | null;
  rank: number | null;
};

/** Composite ranking instruments breakdown. */
export type CompositeInstruments = {
  guitar: InstrumentSkillRank | null;
  bass: InstrumentSkillRank | null;
  drums: InstrumentSkillRank | null;
  vocals: InstrumentSkillRank | null;
  proGuitar: InstrumentSkillRank | null;
  proBass: InstrumentSkillRank | null;
};

/** Composite ranking entry as returned by /api/rankings/composite. */
export type CompositeRankingEntry = {
  accountId: string;
  displayName?: string;
  instrumentsPlayed: number;
  totalSongsPlayed: number;
  compositeRating: number;
  compositeRank: number;
  instruments: CompositeInstruments;
  computedAt: string;
};

/** Paginated composite rankings response. */
export type CompositePageResponse = {
  page: number;
  pageSize: number;
  totalAccounts: number;
  entries: CompositeRankingEntry[];
};

/** Single account composite ranking (includes totalRankedAccounts if needed). */
export type CompositeRankingDto = CompositeRankingEntry;

/** Combo leaderboard entry as returned by /api/rankings/combo. */
export type ComboRankingEntry = {
  rank: number;
  accountId: string;
  displayName?: string;
  adjustedRating: number;
  weightedRating: number;
  fcRate: number;
  totalScore: number;
  maxScorePercent: number;
  songsPlayed: number;
  fullComboCount: number;
  computedAt: string;
};

/** Paginated combo rankings response. */
export type ComboPageResponse = {
  comboId: string;
  rankBy: string;
  page: number;
  pageSize: number;
  totalAccounts: number;
  entries: ComboRankingEntry[];
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

export type RivalSuggestionSong = {
  songId: string;
  instrument: string;
  userRank: number;
  rivalRank: number;
  rankDelta: number;
  userScore: number | null;
  rivalScore: number | null;
};

export type RivalSuggestionEntry = {
  accountId: string;
  displayName: string | null;
  direction: string;
  sharedSongCount: number;
  aheadCount: number;
  behindCount: number;
  songs: RivalSuggestionSong[];
};

export type RivalSuggestionsResponse = {
  accountId: string;
  combo: string;
  computedAt: string | null;
  rivals: RivalSuggestionEntry[];
};

/** Neighbor entry in a per-instrument leaderboard neighborhood. */
export type LeaderboardNeighborEntry = {
  accountId: string;
  displayName?: string | null;
  totalScore: number;
  totalScoreRank: number;
  songsPlayed: number;
  totalChartedSongs: number;
  coverage: number;
  adjustedSkillRating: number;
  adjustedSkillRank: number;
};

/** Per-instrument leaderboard neighborhood response. */
export type LeaderboardNeighborhoodResponse = {
  instrument: string;
  accountId: string;
  rank: number;
  above: LeaderboardNeighborEntry[];
  self: LeaderboardNeighborEntry;
  below: LeaderboardNeighborEntry[];
};

/** Neighbor entry in a composite ranking neighborhood. */
export type CompositeNeighborEntry = {
  accountId: string;
  displayName?: string | null;
  compositeRating: number;
  compositeRank: number;
  instrumentsPlayed: number;
  totalSongsPlayed: number;
};

/** Composite ranking neighborhood response. */
export type CompositeNeighborhoodResponse = {
  accountId: string;
  rank: number;
  above: CompositeNeighborEntry[];
  self: CompositeNeighborEntry;
  below: CompositeNeighborEntry[];
};
