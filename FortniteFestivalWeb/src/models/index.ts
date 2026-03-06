export type SongDifficulty = {
  guitar?: number;
  bass?: number;
  drums?: number;
  vocals?: number;
  proGuitar?: number;
  proBass?: number;
};

export type Song = {
  songId: string;
  title: string;
  artist: string;
  album?: string;
  year?: number;
  tempo?: number;
  albumArt?: string;
  genres?: string[];
  difficulty?: SongDifficulty;
};

export type InstrumentKey =
  | 'Solo_Guitar'
  | 'Solo_Bass'
  | 'Solo_Drums'
  | 'Solo_Vocals'
  | 'Solo_PeripheralGuitar'
  | 'Solo_PeripheralBass';

export const INSTRUMENT_KEYS: InstrumentKey[] = [
  'Solo_Guitar',
  'Solo_Bass',
  'Solo_Drums',
  'Solo_Vocals',
  'Solo_PeripheralGuitar',
  'Solo_PeripheralBass',
];

export const INSTRUMENT_LABELS: Record<InstrumentKey, string> = {
  Solo_Guitar: 'Lead',
  Solo_Bass: 'Bass',
  Solo_Drums: 'Drums',
  Solo_Vocals: 'Vocals',
  Solo_PeripheralGuitar: 'Pro Lead',
  Solo_PeripheralBass: 'Pro Bass',
};

export type LeaderboardEntry = {
  accountId: string;
  displayName?: string;
  score: number;
  rank: number;
  percentile?: number;
  accuracy?: number;
  isFullCombo?: boolean;
  stars?: number;
};

export type SongsResponse = {
  count: number;
  songs: Song[];
};

export type LeaderboardResponse = {
  songId: string;
  instrument: string;
  count: number;
  totalEntries: number;
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
