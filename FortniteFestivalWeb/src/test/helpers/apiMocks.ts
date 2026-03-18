/**
 * Shared API mock factory for page tests.
 *
 * Usage in test files:
 *   vi.mock('../../api/client', () => ({ api: createApiMock() }));
 *
 * Override per-test:
 *   const { api } = await import('../../api/client');
 *   vi.mocked(api.getSongs).mockResolvedValueOnce({ songs: [], count: 0 });
 */
import { vi } from 'vitest';
import type {
  SongsResponse,
  ServerSong,
  LeaderboardResponse,
  LeaderboardEntry,
  AllLeaderboardsResponse,
  PlayerResponse,
  PlayerScore,
  PlayerHistoryResponse,
  ServerScoreHistoryEntry,
  PlayerStatsResponse,
  SyncStatusResponse,
  TrackPlayerResponse,
  AccountSearchResponse,
} from '@festival/core/api/serverTypes';

/* ── Fixture Data ── */

export const MOCK_SONGS: ServerSong[] = [
  {
    songId: 'song-1',
    title: 'Test Song One',
    artist: 'Artist A',
    album: 'Album A',
    year: 2024,
    albumArt: 'https://example.com/art1.jpg',
    genres: ['Rock'],
    difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1, proGuitar: 5, proBass: 3 },
    maxScores: {
      Solo_Guitar: 150000,
      Solo_Bass: 120000,
      Solo_Drums: 180000,
      Solo_Vocals: 100000,
      Solo_PeripheralGuitar: 200000,
      Solo_PeripheralBass: 160000,
    },
  },
  {
    songId: 'song-2',
    title: 'Test Song Two',
    artist: 'Artist B',
    album: 'Album B',
    year: 2023,
    albumArt: 'https://example.com/art2.jpg',
    genres: ['Pop'],
    difficulty: { guitar: 2, bass: 1, drums: 3, vocals: 2, proGuitar: 4, proBass: 2 },
    maxScores: {
      Solo_Guitar: 130000,
      Solo_Bass: 110000,
      Solo_Drums: 160000,
      Solo_Vocals: 90000,
      Solo_PeripheralGuitar: 170000,
      Solo_PeripheralBass: 140000,
    },
  },
  {
    songId: 'song-3',
    title: 'Test Song Three',
    artist: 'Artist C',
    year: 2025,
    difficulty: { guitar: 5, bass: 4, drums: 5, vocals: 3 },
  },
];

export const MOCK_LEADERBOARD_ENTRIES: LeaderboardEntry[] = [
  { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1, percentile: 99, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
  { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2, percentile: 97, accuracy: 98.0, isFullCombo: false, stars: 5, season: 5 },
  { accountId: 'acc-3', displayName: 'Player Three', score: 135000, rank: 3, percentile: 95, accuracy: 96.2, isFullCombo: false, stars: 5, season: 4 },
  { accountId: 'acc-4', displayName: 'Player Four', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
  { accountId: 'acc-5', displayName: 'Player Five', score: 100000, rank: 5, percentile: 80, accuracy: 88.0, isFullCombo: false, stars: 3, season: 3 },
];

export const MOCK_PLAYER_SCORES: PlayerScore[] = [
  { songId: 'song-1', instrument: 'Solo_Guitar', score: 145000, rank: 1, percentile: 99, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5, totalEntries: 500 },
  { songId: 'song-1', instrument: 'Solo_Bass', score: 115000, rank: 5, percentile: 90, accuracy: 95.0, isFullCombo: false, stars: 5, season: 4, totalEntries: 300 },
  { songId: 'song-2', instrument: 'Solo_Guitar', score: 125000, rank: 2, percentile: 97, accuracy: 97.5, isFullCombo: false, stars: 5, season: 5, totalEntries: 400 },
];

export const MOCK_PLAYER: PlayerResponse = {
  accountId: 'test-player-1',
  displayName: 'TestPlayer',
  totalScores: 3,
  scores: MOCK_PLAYER_SCORES,
};

export const MOCK_HISTORY_ENTRIES: ServerScoreHistoryEntry[] = [
  { songId: 'song-1', instrument: 'Solo_Guitar', oldScore: 130000, newScore: 145000, oldRank: 3, newRank: 1, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5, scoreAchievedAt: '2025-01-15T10:00:00Z', changedAt: '2025-01-15T10:00:00Z' },
  { songId: 'song-1', instrument: 'Solo_Guitar', oldScore: 120000, newScore: 130000, oldRank: 5, newRank: 3, accuracy: 97.0, isFullCombo: false, stars: 5, season: 4, scoreAchievedAt: '2024-09-10T08:00:00Z', changedAt: '2024-09-10T08:00:00Z' },
  { songId: 'song-1', instrument: 'Solo_Guitar', newScore: 120000, newRank: 5, accuracy: 93.0, isFullCombo: false, stars: 4, season: 3, scoreAchievedAt: '2024-06-01T12:00:00Z', changedAt: '2024-06-01T12:00:00Z' },
];

export const MOCK_SONGS_RESPONSE: SongsResponse = {
  count: MOCK_SONGS.length,
  currentSeason: 5,
  songs: MOCK_SONGS,
};

export const MOCK_LEADERBOARD_RESPONSE: LeaderboardResponse = {
  songId: 'song-1',
  instrument: 'Solo_Guitar',
  count: MOCK_LEADERBOARD_ENTRIES.length,
  totalEntries: 500,
  localEntries: 500,
  entries: MOCK_LEADERBOARD_ENTRIES,
};

export const MOCK_ALL_LEADERBOARDS_RESPONSE: AllLeaderboardsResponse = {
  songId: 'song-1',
  instruments: [
    { instrument: 'Solo_Guitar', count: 3, totalEntries: 500, localEntries: 500, entries: MOCK_LEADERBOARD_ENTRIES.slice(0, 3) },
    { instrument: 'Solo_Bass', count: 2, totalEntries: 300, localEntries: 300, entries: MOCK_LEADERBOARD_ENTRIES.slice(0, 2) },
    { instrument: 'Solo_Drums', count: 2, totalEntries: 250, localEntries: 250, entries: MOCK_LEADERBOARD_ENTRIES.slice(0, 2) },
    { instrument: 'Solo_Vocals', count: 1, totalEntries: 200, localEntries: 200, entries: MOCK_LEADERBOARD_ENTRIES.slice(0, 1) },
    { instrument: 'Solo_PeripheralGuitar', count: 1, totalEntries: 150, localEntries: 150, entries: MOCK_LEADERBOARD_ENTRIES.slice(0, 1) },
    { instrument: 'Solo_PeripheralBass', count: 1, totalEntries: 100, localEntries: 100, entries: MOCK_LEADERBOARD_ENTRIES.slice(0, 1) },
  ],
};

export const MOCK_PLAYER_HISTORY_RESPONSE: PlayerHistoryResponse = {
  accountId: 'test-player-1',
  count: MOCK_HISTORY_ENTRIES.length,
  history: MOCK_HISTORY_ENTRIES,
};

export const MOCK_PLAYER_STATS_RESPONSE: PlayerStatsResponse = {
  accountId: 'test-player-1',
  stats: [
    { instrument: 'Solo_Guitar', songsPlayed: 10, fullComboCount: 2, goldStarCount: 5, avgAccuracy: 96.5, bestRank: 1, totalScore: 1200000 },
    { instrument: 'Overall', songsPlayed: 25, fullComboCount: 3, goldStarCount: 10, avgAccuracy: 94.2, bestRank: 1, totalScore: 3000000 },
  ],
};

export const MOCK_SYNC_STATUS: SyncStatusResponse = {
  accountId: 'test-player-1',
  isTracked: false,
  backfill: null,
  historyRecon: null,
};

export const MOCK_SYNC_STATUS_TRACKED: SyncStatusResponse = {
  accountId: 'test-player-1',
  isTracked: true,
  backfill: { status: 'completed', songsChecked: 100, totalSongsToCheck: 100, entriesFound: 500, startedAt: '2025-01-01T00:00:00Z', completedAt: '2025-01-01T01:00:00Z' },
  historyRecon: { status: 'completed', songsProcessed: 100, totalSongsToProcess: 100, seasonsQueried: 5, historyEntriesFound: 250, startedAt: '2025-01-01T01:00:00Z', completedAt: '2025-01-01T02:00:00Z' },
};

export const MOCK_TRACK_PLAYER_RESPONSE: TrackPlayerResponse = {
  accountId: 'test-player-1',
  displayName: 'TestPlayer',
  trackingStarted: true,
  backfillStatus: 'queued',
};

export const MOCK_ACCOUNT_SEARCH_RESPONSE: AccountSearchResponse = {
  results: [
    { accountId: 'test-player-1', displayName: 'TestPlayer' },
    { accountId: 'acc-2', displayName: 'Player Two' },
  ],
};

/* ── Mock Factory ── */

export function createApiMock(overrides: Record<string, unknown> = {}) {
  return {
    getSongs: vi.fn().mockResolvedValue(MOCK_SONGS_RESPONSE),
    getLeaderboard: vi.fn().mockResolvedValue(MOCK_LEADERBOARD_RESPONSE),
    getAllLeaderboards: vi.fn().mockResolvedValue(MOCK_ALL_LEADERBOARDS_RESPONSE),
    getPlayer: vi.fn().mockResolvedValue(MOCK_PLAYER),
    getPlayerHistory: vi.fn().mockResolvedValue(MOCK_PLAYER_HISTORY_RESPONSE),
    getPlayerStats: vi.fn().mockResolvedValue(MOCK_PLAYER_STATS_RESPONSE),
    getSyncStatus: vi.fn().mockResolvedValue(MOCK_SYNC_STATUS),
    trackPlayer: vi.fn().mockResolvedValue(MOCK_TRACK_PLAYER_RESPONSE),
    searchAccounts: vi.fn().mockResolvedValue(MOCK_ACCOUNT_SEARCH_RESPONSE),
    getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
    ...overrides,
  };
}
