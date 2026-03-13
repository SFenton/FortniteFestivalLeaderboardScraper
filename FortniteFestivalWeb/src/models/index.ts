/**
 * Web app model re-exports from the shared @festival/core package.
 *
 * The canonical type definitions live in packages/core/src/api/serverTypes.ts.
 * This file re-exports them with web-app-friendly aliases so existing imports
 * like `import { Song, InstrumentKey } from '../models'` continue to work.
 *
 * We import from the specific sub-path rather than the barrel '@festival/core'
 * to avoid pulling in React Native dependencies during type checking.
 */

export type {
  SongDifficulty,
  ServerSong as Song,
  LeaderboardEntry,
  SongsResponse,
  LeaderboardResponse,
  PlayerScore,
  PlayerResponse,
  AccountCheckResponse,
  AccountSearchResult,
  AccountSearchResponse,
  ScrapeProgress,
  FirstSeenEntry,
  FirstSeenResponse,
  LeaderboardPopulationEntry,
  TrackPlayerResponse,
  SyncStatusResponse,
  ServerScoreHistoryEntry as ScoreHistoryEntry,
  PlayerHistoryResponse,
  ServerInstrumentKey as InstrumentKey,
  AllLeaderboardsResponse,
  PlayerStatEntry,
  PlayerStatsResponse,
} from '@festival/core/api/serverTypes';

export {
  SERVER_INSTRUMENT_KEYS as INSTRUMENT_KEYS,
  SERVER_INSTRUMENT_LABELS as INSTRUMENT_LABELS,
} from '@festival/core/api/serverTypes';
