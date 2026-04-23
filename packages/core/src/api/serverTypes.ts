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
  | 'Solo_PeripheralBass'
  | 'Solo_PeripheralVocals'
  | 'Solo_PeripheralCymbals'
  | 'Solo_PeripheralDrums';

export const SERVER_INSTRUMENT_KEYS: ServerInstrumentKey[] = [
  'Solo_Guitar',
  'Solo_Bass',
  'Solo_Drums',
  'Solo_Vocals',
  'Solo_PeripheralGuitar',
  'Solo_PeripheralBass',
  'Solo_PeripheralVocals',
  'Solo_PeripheralCymbals',
  'Solo_PeripheralDrums',
];

export const SERVER_INSTRUMENT_LABELS: Record<ServerInstrumentKey, string> = {
  Solo_Guitar: 'Lead',
  Solo_Bass: 'Bass',
  Solo_Drums: 'Drums',
  Solo_Vocals: 'Tap Vocals',
  Solo_PeripheralGuitar: 'Pro Lead',
  Solo_PeripheralBass: 'Pro Bass',
  Solo_PeripheralVocals: 'Mic Mode',
  Solo_PeripheralCymbals: 'Pro Drums + Cymbals',
  Solo_PeripheralDrums: 'Pro Drums',
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
  proDrums?: number;
  proCymbals?: number;
  proVocals?: number;
};

export const SERVER_SONG_DIFFICULTY_KEYS: Record<ServerInstrumentKey, keyof SongDifficulty> = {
  Solo_Guitar: 'guitar',
  Solo_Bass: 'bass',
  Solo_Drums: 'drums',
  Solo_Vocals: 'vocals',
  Solo_PeripheralGuitar: 'proGuitar',
  Solo_PeripheralBass: 'proBass',
  Solo_PeripheralVocals: 'proVocals',
  Solo_PeripheralCymbals: 'proCymbals',
  Solo_PeripheralDrums: 'proDrums',
};

export function isChartedServerDifficulty(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 && value !== 99;
}

/** Song as returned by the FSTService /api/songs endpoint. */
export type ServerSong = {
  songId: string;
  title: string;
  artist: string;
  album?: string;
  year?: number;
  tempo?: number;
  /** Lead instrument signature: "Guitar" or "Keyboard". Controls lead/pro-lead icon variant. */
  sig?: string;
  /** Song duration in seconds, from Epic's spark-tracks `dn` field. Null when missing or 0. */
  durationSeconds?: number;
  albumArt?: string;
  genres?: string[];
  difficulty?: SongDifficulty;
  maxScores?: Partial<Record<ServerInstrumentKey, number>>;
  /** Population tiers per instrument for client-side filtered-total computation. */
  populationTiers?: Partial<Record<ServerInstrumentKey, PopulationTierData>> | null;
};

export function getServerSongInstrumentDifficulty(
  song: ServerSong,
  instrument: ServerInstrumentKey | null | undefined,
): number | undefined {
  if (!instrument) return undefined;

  const difficultyKey = SERVER_SONG_DIFFICULTY_KEYS[instrument];
  const difficulty = song.difficulty?.[difficultyKey];
  return isChartedServerDifficulty(difficulty) ? difficulty : undefined;
}

export function serverSongSupportsInstrument(
  song: ServerSong,
  instrument: ServerInstrumentKey | null | undefined,
): boolean {
  return getServerSongInstrumentDifficulty(song, instrument) != null;
}

/** Song as returned by the /api/shop endpoint (enriched with catalog metadata). */
export type ShopSong = {
  songId: string;
  title: string;
  artist: string;
  year?: number;
  albumArt?: string;
  shopUrl: string;
  leavingTomorrow?: boolean;
};

export type ShopResponse = {
  songs: ShopSong[];
  lastUpdated?: string;
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
  added: ShopSong[];
  removed: string[];
  total: number;
  leavingTomorrow: string[];
};

export type ShopSnapshotMessage = {
  type: 'shop_snapshot';
  songs: ShopSong[];
  total: number;
  leavingTomorrow: string[];
};

export type SyncProgressMessage = {
  type: 'sync_progress';
  accountId: string;
  phase: 'backfill' | 'history' | 'rivals' | 'postscrape' | 'complete' | 'error';
  itemsCompleted: number;
  totalItems: number;
  entriesFound: number;
  currentSongName?: string;
  seasonsQueried?: number;
  rivalsFound?: number;
  elapsedSeconds?: number;
  /** Whether the adaptive limiter has significantly reduced DOP (CDN throttle). */
  isThrottled?: boolean;
  /** Status key for throttle reason (e.g. "throttle_cdn_busy"). Frontend translates locally. */
  throttleStatusKey?: string;
  /** True when sync is complete and global ranks have not yet been recalculated. */
  pendingRankUpdate?: boolean;
  /** Estimated minutes until next global ranking pass. */
  estimatedRankUpdateMinutes?: number;
  /** Status key for CDN probe state (e.g. "probe_retrying", "probe_waiting"). */
  probeStatusKey?: string;
  /** Seconds until next probe retry (during probe_waiting state). */
  nextRetrySeconds?: number;
  /** Current probe attempt number (1-based). */
  probeAttempt?: number;
};

export type WsNotificationMessage =
  | ShopChangedMessage
  | ShopSnapshotMessage
  | SyncProgressMessage
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
  /** Player's position within locally-stored data (ROW_NUMBER over our DB). */
  localRank?: number;
  percentile?: number;
  accuracy?: number;
  isFullCombo?: boolean;
  stars?: number;
  season?: number;
  difficulty?: number;
  endTime?: string;
  totalEntries?: number;
  /** ISO 8601 timestamp of the most recent score_history entry (any leeway). */
  lastPlayedAt?: string;
  /** ISO 8601 timestamp of the most recent *valid* score_history entry (respects CHOpt threshold). */
  validLastPlayedAt?: string;
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
  estimatedSeason?: number;
  calculationVersion?: number;
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
  backfillKicked: boolean;
};

export type SyncStatusResponse = {
  accountId: string;
  isTracked: boolean;
  backfill: {
    status: string;
    songsChecked: number;
    totalSongsToCheck: number;
    entriesFound: number;
    currentSongName?: string;
    startedAt: string | null;
    completedAt: string | null;
  } | null;
  historyRecon: {
    status: string;
    songsProcessed: number;
    totalSongsToProcess: number;
    seasonsQueried: number;
    historyEntriesFound: number;
    currentSongName?: string;
    startedAt: string | null;
    completedAt: string | null;
  } | null;
  rivals: {
    status: string;
    combosComputed: number;
    totalCombosToCompute: number;
    rivalsFound: number;
    startedAt: string | null;
    completedAt: string | null;
  } | null;
};

export type ServiceInfoResponse = {
  lastCompletedUpdate: {
    startedAt: string;
    completedAt: string | null;
  } | null;
  currentUpdate: {
    status: 'idle' | 'updating';
    startedAt: string | null;
    phase: string | null;
    subOperation: string | null;
    /** Aggregate phase percent (0-100). Null when not yet determinable. */
    progressPercent?: number | null;
    /** Wall-clock seconds since the current phase began. */
    elapsedSeconds?: number | null;
    /** Estimated remaining seconds for the current phase. Null when no estimate. */
    estimatedRemainingSeconds?: number | null;
    /** Parallel branches inside the current phase (e.g. enrichment, finalize). */
    branches?: ServiceInfoBranch[] | null;
  };
  nextScheduledUpdateAt: string | null;
};

/** A single named branch within a phase. Multiple branches run in parallel. */
export type ServiceInfoBranch = {
  /** Stable snake_case branch identifier (e.g. "rank_recompute"). */
  id: string;
  /** Lifecycle status. */
  status: 'pending' | 'running' | 'complete' | 'skipped' | 'failed';
  startedAtUtc: string | null;
  completedAtUtc: string | null;
  /** Items completed; null when the branch reports no counters. */
  completed: number | null;
  /** Total items planned; null when the branch reports no counters. */
  total: number | null;
  /** Optional human-readable summary. */
  message: string | null;
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

/** A single leeway breakpoint tier with pre-computed statistics. */
export type PlayerStatsTier = {
  minLeeway: number | null;
  songsPlayed: number;
  overThresholdCount: number;
  fcCount: number;
  fcPercent: number;
  goldStarCount: number;
  fiveStarCount: number;
  fourStarCount: number;
  threeStarCount: number;
  twoStarCount: number;
  oneStarCount: number;
  avgAccuracy: number;
  bestAccuracy: number;
  averageStars: number;
  avgScore: number;
  totalScore: number;
  completionPercent: number;
  bestRank: number;
  bestRankSongId?: string | null;
  percentileDist?: string | null;
  avgPercentile?: string | null;
  overallPercentile?: string | null;
  topSongs?: string | null;
  bottomSongs?: string | null;
  bestRankInstrument?: string | null;
};

export type PlayerStatsInstrument = {
  instrument: string;
  tiers: PlayerStatsTier[];
};

export type PlayerStatsResponse = {
  accountId: string;
  totalSongs: number;
  instruments: PlayerStatsInstrument[];
  compositeRanks?: CompositeRanks | null;
  instrumentRanks?: InstrumentRankEntry[] | null;
  bands?: PlayerBandsResponse | null;
};

export type PlayerBandType = 'Band_Duets' | 'Band_Trios' | 'Band_Quad';

export type PlayerBandMember = {
  accountId: string;
  displayName?: string | null;
  instruments: ServerInstrumentKey[];
};

export type PlayerBandEntry = {
  teamKey: string;
  bandType: PlayerBandType;
  members: PlayerBandMember[];
};

export type PlayerBandGroup = {
  totalCount: number;
  entries: PlayerBandEntry[];
};

export type PlayerBandsResponse = {
  all: PlayerBandGroup;
  duos: PlayerBandGroup;
  trios: PlayerBandGroup;
  quads: PlayerBandGroup;
};

export type PlayerBandTypeResponse = {
  accountId: string;
  bandType: PlayerBandType;
  comboId?: string | null;
  totalCount: number;
  entries: PlayerBandEntry[];
};

/** Flat composite rank numbers embedded in the stats response. */
export type CompositeRanks = {
  adjusted: number;
  weighted?: number | null;
  fcRate?: number | null;
  totalScore?: number | null;
  maxScore?: number | null;
};

/** Per-instrument rank entry with base ranks and leeway-responsive tiers. */
export type InstrumentRankEntry = {
  ins: string;
  totalRanked: number;
  base: InstrumentRankBase;
  tiers: InstrumentRankTier[];
};

/** Base rank values at the most restrictive leeway (-5.0%). */
export type InstrumentRankBase = {
  adjusted: number;
  weighted: number;
  fcRate: number;
  totalScore: number;
  maxScore: number;
};

/** Sparse rank tier — only changed fields present. l=null means unfiltered. */
export type InstrumentRankTier = {
  l: number | null;
  adjusted?: number;
  weighted?: number;
  fcRate?: number;
  totalScore?: number;
  maxScore?: number;
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

export type BandRankingMetric = Exclude<RankingMetric, 'maxscore'>;

export type BandType = 'Band_Duets' | 'Band_Trios' | 'Band_Quad';

export type BandTeamMember = {
  accountId: string;
  displayName?: string | null;
};

export type BandRankingEntry = {
  teamKey: string;
  teamMembers: BandTeamMember[];
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
  avgAccuracy: number;
  fullComboCount: number;
  avgStars: number;
  bestRank: number;
  avgRank: number;
  rawWeightedRating: number | null;
  computedAt: string;
};

export type BandRankingsPageResponse = {
  bandType: BandType;
  comboId?: string | null;
  rankBy: BandRankingMetric;
  page: number;
  pageSize: number;
  totalTeams: number;
  entries: BandRankingEntry[];
};

export type BandRankingDto = BandRankingEntry & {
  bandType: BandType;
  comboId?: string | null;
  totalRankedTeams: number;
};

export type BandComboCatalogEntry = {
  comboId: string;
  instruments: ServerInstrumentKey[];
  teamCount: number;
};

export type BandComboCatalogResponse = {
  bandType: BandType;
  combos: BandComboCatalogEntry[];
};

/** Daily rank history snapshot as returned by /api/rankings/{instrument}/{accountId}/history. */
export type RankHistoryEntry = {
  snapshotDate: string;
  snapshotTakenAt?: string | null;
  isSynthetic?: boolean;
  adjustedSkillRank: number;
  weightedRank: number;
  fcRateRank: number;
  totalScoreRank: number;
  maxScorePercentRank: number;
  adjustedSkillRating: number | null;
  weightedRating: number | null;
  fcRate: number | null;
  totalScore: number | null;
  maxScorePercent: number | null;
  songsPlayed: number | null;
  coverage: number | null;
  fullComboCount: number | null;
  rawMaxScorePercent: number | null;
  rawWeightedRating: number | null;
  rawSkillRating: number | null;
};

/** Response from /api/rankings/{instrument}/{accountId}/history. */
export type RankHistoryResponse = {
  instrument: string;
  accountId: string;
  history: RankHistoryEntry[];
  /** Present only for leeway-filtered requests — sparse rank delta entries. */
  deltas?: RankHistoryDeltaEntry[];
};

/** Daily rank delta entry for a specific leeway bucket. */
export type RankHistoryDeltaEntry = {
  snapshotDate: string;
  adjustedRankDelta: number;
  weightedRankDelta: number;
  fcRateRankDelta: number;
  totalScoreRankDelta: number;
  maxScoreRankDelta: number;
};

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
  rawMaxScorePercent: number | null;
  rawWeightedRating: number | null;
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
  compositeRatingWeighted?: number | null;
  compositeRankWeighted?: number | null;
  compositeRatingFcRate?: number | null;
  compositeRankFcRate?: number | null;
  compositeRatingTotalScore?: number | null;
  compositeRankTotalScore?: number | null;
  compositeRatingMaxScore?: number | null;
  compositeRankMaxScore?: number | null;
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

/** Song sample with indexed songId reference for rivals-all response. */
export type RivalsAllSample = {
  /** Index into the top-level songs[] array. */
  s: number;
  /** Instrument key (e.g. "Solo_Guitar"). */
  i: string;
  /** User rank on this song. */
  ur: number;
  /** Rival rank on this song. */
  rr: number;
  /** User score (null if unknown). */
  us: number | null;
  /** Rival score (null if unknown). */
  rs: number | null;
};

/** Rival entry in the rivals-all response (includes song samples). */
export type RivalsAllEntry = {
  accountId: string;
  displayName: string | null;
  direction: string;
  sharedSongCount: number;
  aheadCount: number;
  behindCount: number;
  rivalScore: number;
  samples: RivalsAllSample[];
};

/** Combo data in the rivals-all response. */
export type RivalsAllCombo = {
  combo: string;
  above: RivalsAllEntry[];
  below: RivalsAllEntry[];
};

/** Response from /api/player/{accountId}/rivals/all (precomputed, includes indexed song samples). */
export type RivalsAllResponse = {
  accountId: string;
  /** Deduplicated song ID index. Samples reference these by integer index. */
  songs: string[];
  combos: RivalsAllCombo[];
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

// ═══════════════════════════════════════════════════════════════
// Compact wire format types & transforms
// ═══════════════════════════════════════════════════════════════
//
// The API transmits a bandwidth-optimised format with short field names,
// instrument hex codes, accuracy ÷ 1000, and leeway × 10. The functions
// below expand them back to the canonical types above so consumers need
// no changes.

/** Decode a single-instrument hex combo ID to the canonical instrument key. */
function instrumentFromHex(hex: string): ServerInstrumentKey {
  const mask = parseInt(hex, 16);
  for (let bit = 0; bit < SERVER_INSTRUMENT_KEYS.length; bit++) {
    if (mask & (1 << bit)) return SERVER_INSTRUMENT_KEYS[bit];
  }
  return SERVER_INSTRUMENT_KEYS[0]; // fallback
}

// ─── Player wire types ───────────────────────────────────────

/** Wire format of a player score (compact keys). */
type WirePlayerScore = {
  si: string;
  ins: string;
  sc: number;
  acc: number;
  fc: boolean;
  st: number;
  dif: number;
  sn: number;
  pct: number;
  rk: number;
  lrk?: number;
  et?: string;
  te: number;
  lp?: string;
  vlp?: string;
  ml?: number | null;
  vs?: WireValidScore[] | null;
  // Legacy fallback fields (non-precomputed path only)
  isValid?: boolean | null;
  validScore?: number | null;
  validAccuracy?: number | null;
  validIsFullCombo?: boolean | null;
  validStars?: number | null;
  validRank?: number | null;
  validTotalEntries?: number | null;
};

/** Wire format of a valid score variant. */
type WireValidScore = {
  sc: number;
  acc?: number | null;
  fc?: boolean | null;
  st?: number | null;
  ml: number;
  rt?: WireRankTier[] | null;
};

/** Wire format of a rank tier changepoint. */
type WireRankTier = { l: number; r: number };

/** Wire format of the full player response. */
type WirePlayerResponse = {
  accountId: string;
  displayName: string;
  totalScores: number;
  scores: WirePlayerScore[];
};

// ─── Population tier wire types (inside /api/songs) ─────────

/** Wire format of PopulationTierData. */
type WirePopulationTierData = { bc: number; t: WirePopulationTier[] };

/** Wire format of a single population tier changepoint. */
type WirePopulationTier = { l: number; t: number };

/** Wire format of a song (only populationTiers changes). */
type WireSong = Omit<ServerSong, 'populationTiers'> & {
  populationTiers?: Partial<Record<ServerInstrumentKey, WirePopulationTierData>> | null;
};

/** Wire format of the songs response. */
type WireSongsResponse = Omit<SongsResponse, 'songs'> & { songs: WireSong[] };

// ─── Transform: player ───────────────────────────────────────

function expandRankTier(w: WireRankTier): RankTier {
  return { leeway: w.l, rank: w.r };
}

function expandValidScore(w: WireValidScore): ValidScoreVariant {
  return {
    score: w.sc,
    accuracy: w.acc != null ? w.acc * 1000 : w.acc,
    fc: w.fc,
    stars: w.st,
    minLeeway: w.ml,
    rankTiers: w.rt?.map(expandRankTier),
  };
}

function expandPlayerScore(w: WirePlayerScore): PlayerScore {
  return {
    songId: w.si,
    instrument: instrumentFromHex(w.ins),
    score: w.sc,
    accuracy: w.acc * 1000,
    isFullCombo: w.fc,
    stars: w.st,
    difficulty: w.dif,
    season: w.sn,
    percentile: w.pct,
    rank: w.rk,
    localRank: w.lrk || undefined,
    endTime: w.et,
    totalEntries: w.te,
    lastPlayedAt: w.lp,
    validLastPlayedAt: w.vlp,
    minLeeway: w.ml,
    validScores: w.vs?.map(expandValidScore),
    // Legacy fallback fields (pass through, scaling accuracy)
    isValid: w.isValid,
    validScore: w.validScore,
    validRank: w.validRank,
    validAccuracy: w.validAccuracy != null ? w.validAccuracy * 1000 : w.validAccuracy,
    validIsFullCombo: w.validIsFullCombo,
    validStars: w.validStars,
    validTotalEntries: w.validTotalEntries,
  };
}

/** Expand a compact wire player response to the canonical PlayerResponse. */
export function expandWirePlayerResponse(wire: WirePlayerResponse): PlayerResponse {
  return {
    accountId: wire.accountId,
    displayName: wire.displayName,
    totalScores: wire.totalScores,
    scores: wire.scores.map(expandPlayerScore),
  };
}

// ─── Transform: songs (population tiers only) ────────────────

function expandPopulationTier(w: WirePopulationTier): PopulationTier {
  return { leeway: w.l, total: w.t };
}

function expandPopulationTierData(w: WirePopulationTierData): PopulationTierData {
  return { baseCount: w.bc, tiers: w.t.map(expandPopulationTier) };
}

function expandSong(w: WireSong): ServerSong {
  if (!w.populationTiers) return w as ServerSong;
  const expanded: Partial<Record<ServerInstrumentKey, PopulationTierData>> = {};
  for (const [key, val] of Object.entries(w.populationTiers)) {
    if (val) expanded[key as ServerInstrumentKey] = expandPopulationTierData(val);
  }
  return { ...w, populationTiers: expanded } as ServerSong;
}

/** Expand a compact wire songs response to the canonical SongsResponse. */
export function expandWireSongsResponse(wire: WireSongsResponse): SongsResponse {
  return {
    ...wire,
    songs: wire.songs.map(expandSong),
  };
}

// ─── Stats wire types ────────────────────────────────────────

/** Known percentile bucket values, matching server PercentileBuckets. */
const PERCENTILE_BUCKETS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;

/** Wire format of a single stats tier. */
type WireStatsTier = {
  ml: number | null;
  sp: number;
  otc: number;
  fcc: number;
  fcp: number;
  s6: number;
  s5: number;
  s4: number;
  s3: number;
  s2: number;
  s1: number;
  aa: number; // avgAccuracy ÷1000 (0-1000)
  ba: number; // bestAccuracy ÷1000 (0-1000)
  ast: number;
  as: number;
  tsc: number;
  cp: number;
  br: number;
  brs?: string | null;
  pd?: number[] | null; // 17-element int array
  ap?: number | null;   // bucket index
  op?: number | null;   // bucket index
  ts?: string | null;   // grouped songs JSON or null (inherit)
  bs?: string | null;   // grouped songs JSON or null (inherit)
  bri?: string | null;  // instrument hex (Overall only)
};

/** Wire format of a stats instrument group. */
type WireStatsInstrument = {
  ins: string; // hex instrument code ("01", "00" for Overall)
  tiers: WireStatsTier[];
};

/** Wire format of the stats response. */
type WireStatsResponse = {
  accountId: string;
  totalSongs: number;
  instruments: WireStatsInstrument[];
  compositeRanks?: CompositeRanks | null;
  bands?: PlayerBandsResponse | null;
};

function expandPercentileIndex(idx: number | null | undefined): string | null {
  if (idx == null) return null;
  return `Top ${PERCENTILE_BUCKETS[idx] ?? 100}%`;
}

function expandPercentileDist(arr: number[] | null | undefined): string | null {
  if (!arr || arr.length === 0) return null;
  const obj: Record<string, number> = {};
  for (let i = 0; i < PERCENTILE_BUCKETS.length && i < arr.length; i++) {
    obj[String(PERCENTILE_BUCKETS[i])] = arr[i];
  }
  return JSON.stringify(obj);
}

type GroupedSong = { p: number; s: string[] };

function expandGroupedSongs(json: string | null | undefined): string | null {
  if (!json) return null;
  try {
    const groups = JSON.parse(json) as GroupedSong[];
    const flat = groups.flatMap(g =>
      g.s.map(id => ({ songId: id, percentile: g.p }))
    );
    return JSON.stringify(flat);
  } catch {
    return json; // pass through if already in old format
  }
}

function expandStatsTier(w: WireStatsTier, prevTopSongs: string | null, prevBottomSongs: string | null): PlayerStatsTier {
  const topSongs = w.ts != null ? expandGroupedSongs(w.ts) : prevTopSongs;
  const bottomSongs = w.bs != null ? expandGroupedSongs(w.bs) : prevBottomSongs;
  return {
    minLeeway: w.ml,
    songsPlayed: w.sp,
    overThresholdCount: w.otc,
    fcCount: w.fcc,
    fcPercent: w.fcp,
    goldStarCount: w.s6,
    fiveStarCount: w.s5,
    fourStarCount: w.s4,
    threeStarCount: w.s3,
    twoStarCount: w.s2,
    oneStarCount: w.s1,
    avgAccuracy: w.aa * 1000, // expand 0-1000 back to 0-1,000,000 for ACCURACY_SCALE compat
    bestAccuracy: w.ba * 1000,
    averageStars: w.ast,
    avgScore: w.as,
    totalScore: w.tsc,
    completionPercent: w.cp,
    bestRank: w.br,
    bestRankSongId: w.brs,
    percentileDist: expandPercentileDist(w.pd),
    avgPercentile: expandPercentileIndex(w.ap),
    overallPercentile: expandPercentileIndex(w.op),
    topSongs,
    bottomSongs,
    bestRankInstrument: w.bri != null ? instrumentFromHex(w.bri) : w.bri,
  };
}

function expandStatsInstrument(w: WireStatsInstrument): PlayerStatsInstrument {
  const instrument = w.ins === '00' ? 'Overall' : instrumentFromHex(w.ins);
  const tiers: PlayerStatsTier[] = [];
  let prevTop: string | null = null;
  let prevBottom: string | null = null;
  for (const t of w.tiers) {
    const expanded = expandStatsTier(t, prevTop, prevBottom);
    tiers.push(expanded);
    if (expanded.topSongs != null) prevTop = expanded.topSongs;
    if (expanded.bottomSongs != null) prevBottom = expanded.bottomSongs;
  }
  return { instrument, tiers };
}

/** Expand a compact wire stats response to the canonical PlayerStatsResponse. */
export function expandWireStatsResponse(wire: WireStatsResponse): PlayerStatsResponse {
  return {
    accountId: wire.accountId,
    totalSongs: wire.totalSongs ?? 0,
    instruments: (wire.instruments ?? []).map(expandStatsInstrument),
    compositeRanks: wire.compositeRanks ?? null,
    bands: wire.bands ?? null,
  };
}
