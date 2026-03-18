/**
 * Shared enums for UI state and feature-specific types.
 * These live in @festival/core so they can be shared between
 * the web app and the mobile app when rebuilt.
 */

/** UI animation state machine phase for page transitions. */
export enum LoadPhase {
  Loading = 'loading',
  SpinnerOut = 'spinnerOut',
  ContentIn = 'contentIn',
}

/** Sort modes for player score history views. */
export enum PlayerScoreSortMode {
  Date = 'date',
  Score = 'score',
  Accuracy = 'accuracy',
  Season = 'season',
}

/** Path/chart difficulty levels. */
export enum Difficulty {
  Easy = 'easy',
  Medium = 'medium',
  Hard = 'hard',
  Expert = 'expert',
}

/** All difficulty values as an ordered array. */
export const DIFFICULTIES: readonly Difficulty[] = [
  Difficulty.Easy,
  Difficulty.Medium,
  Difficulty.Hard,
  Difficulty.Expert,
];

/** ScoreHistoryChart card animation phase. */
export enum CardPhase {
  Closed = 'closed',
  Growing = 'growing',
  Open = 'open',
  Fading = 'fading',
  Shrinking = 'shrinking',
  SwapOut = 'swapOut',
  SwapIn = 'swapIn',
}

/** ScoreCardList animation phase. */
export enum ListPhase {
  Idle = 'idle',
  In = 'in',
  Out = 'out',
}

/** PathsModal image loading phase. */
export enum ImagePhase {
  FadeOutImage = 'fadeOutImage',
  Spinner = 'spinner',
  FadeOutSpinner = 'fadeOutSpinner',
  ImageReady = 'imageReady',
  FadeInImage = 'fadeInImage',
  Idle = 'idle',
}

/** Floating action button mode. */
export enum FabMode {
  Players = 'players',
  Songs = 'songs',
}

/** Leaderboard animation mode. */
export enum AnimMode {
  First = 'first',
  Paginate = 'paginate',
  Cached = 'cached',
}

/** Navigation tab identifiers. */
export enum TabKey {
  Songs = 'songs',
  Suggestions = 'suggestions',
  Statistics = 'statistics',
  Settings = 'settings',
}

/** Suggestion category row layout mode. */
export enum RowLayout {
  InstrumentChips = 'instrumentChips',
  SingleInstrument = 'singleInstrument',
  Percentile = 'percentile',
  Season = 'season',
  UnfcAccuracy = 'unfcAccuracy',
  Hidden = 'hidden',
}

/** Sync status phase for backfill/history reconstruction. */
export enum SyncPhase {
  Idle = 'idle',
  Backfill = 'backfill',
  History = 'history',
  Complete = 'complete',
  Error = 'error',
}

/** Backfill/history reconstruction task status. */
export enum BackfillStatus {
  Pending = 'pending',
  InProgress = 'in_progress',
  Complete = 'complete',
  Error = 'error',
}

/** Predefined size scale for InstrumentHeader rendering. */
export enum InstrumentHeaderSize {
  XS = 'xs',
  SM = 'sm',
  MD = 'md',
  LG = 'lg',
  XL = 'xl',
}

/** Percentile tier classification for badge display. */
export enum PercentileTier {
  Top1 = 'Top 1%',
  Top5 = 'Top 5%',
  Top10 = 'Top 10%',
  Top25 = 'Top 25%',
  Top50 = 'Top 50%',
}

/**
 * Accuracy values from the API are stored as percent × 10,000.
 * Divide by this constant to get the human-readable percentage.
 * e.g. 987600 / ACCURACY_SCALE = 98.76%
 */
export const ACCURACY_SCALE = 10_000;
