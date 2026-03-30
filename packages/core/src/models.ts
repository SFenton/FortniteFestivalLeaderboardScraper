import type {InstrumentKey} from './instruments';

export type TrackIntensities = {
  gr?: number; // lead/guitar
  ba?: number;
  ds?: number;
  vl?: number;
  pg?: number; // plastic guitar
  pb?: number;
  pd?: number;
  bd?: number; // pro vocals (optional)
};

export type Track = {
  su: string; // song id
  tt?: string; // title
  an?: string; // artist
  au?: string; // artwork url
  ry?: number; // release year
  mt?: number; // tempo
  in?: TrackIntensities;
};

export type Song = {
  _title?: string;
  track: Track;
  _activeDate?: string; // ISO date in our TS model
  lastModified?: string; // ISO date
  isSelected?: boolean;
  imagePath?: string;
  /** CHOpt theoretical max scores per instrument (from PathDataStore). */
  maxScores?: Partial<Record<InstrumentKey, number>>;
};

export type V1TrackedStats = {
  SCORE?: number;
  ACCURACY?: number;
  FULL_COMBO?: number;
  STARS_EARNED?: number;
  SEASON?: number;
  DIFFICULTY?: number;
};

export type V1SessionHistory = {
  trackedStats?: V1TrackedStats;
};

export type V1LeaderboardEntry = {
  team_id?: string;
  rank?: number;
  pointsEarned?: number;
  score?: number;
  percentile?: number;
  sessionHistory?: V1SessionHistory[];
};

export type V1LeaderboardPage = {
  page: number;
  totalPages: number;
  entries: V1LeaderboardEntry[];
};

// Game difficulty the high score was achieved on (from API DIFFICULTY field).
// 0 = Easy, 1 = Medium, 2 = Hard, 3 = Expert, -1 = unknown/not yet fetched.
export type GameDifficulty = -1 | 0 | 1 | 2 | 3;

export const GAME_DIFFICULTY_LABELS: Record<GameDifficulty, string> = {
  [-1]: 'Unknown',
  [0]: 'Easy',
  [1]: 'Medium',
  [2]: 'Hard',
  [3]: 'Expert',
};

export class ScoreTracker {
  initialized = false;
  maxScore = 0;
  difficulty = 0;
  /** Game difficulty the high score was played on: 0=Easy, 1=Medium, 2=Hard, 3=Expert, -1=unknown */
  gameDifficulty: GameDifficulty = -1;
  numStars = 0;
  isFullCombo = false;
  // Stored as “percent * 10,000”. Example: 98.76% => 987,600.
  percentHit = 0;
  seasonAchieved = 0;
  rank = 0;
  totalEntries = 0;
  rawPercentile = 0;
  calculatedNumEntries = 0;

  percentHitFormatted = '';
  starsFormatted = '';
  leaderboardPercentileFormatted = '';

  refreshDerived(): void {
    this.percentHitFormatted = `${(this.percentHit / 10000).toFixed(2)}%`;

    if (this.numStars <= 0) {
      this.starsFormatted = 'N/A';
    } else {
      // Keep it UI-agnostic (string only). Use '*' as a portable star glyph.
      this.starsFormatted = '*'.repeat(Math.min(this.numStars, 6));
    }

    if (this.rawPercentile > 0) {
      // rawPercentile is a fraction like 0.0144 meaning “top ~1.44%”.
      let topPct = this.rawPercentile * 100;
      if (topPct > 100) topPct = 100;
      if (topPct < 1) topPct = 1;
      const thresholds = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100];
      const bucket = thresholds.find(t => topPct <= t) ?? 100;
      this.leaderboardPercentileFormatted = `Top ${bucket}%`;
    } else {
      this.leaderboardPercentileFormatted = '';
    }
  }
}

export type LeaderboardData = {
  title?: string;
  artist?: string;
  songId: string;
  guitar?: ScoreTracker;
  drums?: ScoreTracker;
  bass?: ScoreTracker;
  vocals?: ScoreTracker;
  pro_guitar?: ScoreTracker;
  pro_bass?: ScoreTracker;
  dirty?: boolean;
  correlatedV1Pages?: Partial<Record<InstrumentKey, V1LeaderboardPage>>;
};

/**
 * A single row from the ScoreHistory table — represents one score change event.
 * Matches the personal DB schema produced by FSTService's PersonalDbBuilder.
 */
export type ScoreHistoryEntry = {
  id?: number;
  songId: string;
  instrument: string; // e.g. 'Solo_Guitar', 'Solo_Bass', etc.
  oldScore?: number;
  newScore?: number;
  oldRank?: number;
  newRank?: number;
  accuracy?: number;
  isFullCombo?: boolean;
  stars?: number;
  percentile?: number;
  season?: number;
  scoreAchievedAt?: string; // ISO 8601 timestamp
  seasonRank?: number;
  allTimeRank?: number;
  changedAt: string; // ISO 8601 timestamp
};
