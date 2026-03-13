import type {
  SongsResponse,
  LeaderboardResponse,
  PlayerResponse,
  AccountSearchResponse,
  TrackPlayerResponse,
  SyncStatusResponse,
  PlayerHistoryResponse,
  InstrumentKey,
  AllLeaderboardsResponse,
  PlayerStatsResponse,
} from '../models';

const BASE = '';

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`);
  if (!res.ok) {
    throw new Error(`API ${res.status}: ${res.statusText}`);
  }
  return res.json() as Promise<T>;
}

async function post<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
  });
  if (!res.ok) {
    throw new Error(`API ${res.status}: ${res.statusText}`);
  }
  return res.json() as Promise<T>;
}

export const api = {
  getSongs: () => get<SongsResponse>('/api/songs'),

  getLeaderboard: (songId: string, instrument: InstrumentKey, top = 100, offset = 0) =>
    get<LeaderboardResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/${encodeURIComponent(instrument)}?top=${top}&offset=${offset}`,
    ),

  getPlayer: (accountId: string, songId?: string) =>
    get<PlayerResponse>(
      `/api/player/${encodeURIComponent(accountId)}${songId ? `?songId=${encodeURIComponent(songId)}` : ''}`,
    ),

  searchAccounts: (q: string, limit = 10) =>
    get<AccountSearchResponse>(
      `/api/account/search?q=${encodeURIComponent(q)}&limit=${limit}`,
    ),

  trackPlayer: (accountId: string) =>
    post<TrackPlayerResponse>(`/api/player/${encodeURIComponent(accountId)}/track`),

  getSyncStatus: (accountId: string) =>
    get<SyncStatusResponse>(`/api/player/${encodeURIComponent(accountId)}/sync-status`),

  getPlayerHistory: (accountId: string, songId?: string) =>
    get<PlayerHistoryResponse>(
      `/api/player/${encodeURIComponent(accountId)}/history${songId ? `?songId=${encodeURIComponent(songId)}` : ''}`,
    ),

  getAllLeaderboards: (songId: string, top = 10) =>
    get<AllLeaderboardsResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/all?top=${top}`,
    ),

  getPlayerStats: (accountId: string) =>
    get<PlayerStatsResponse>(
      `/api/player/${encodeURIComponent(accountId)}/stats`,
    ),
};
