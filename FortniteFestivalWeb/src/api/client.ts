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

import { cachedFetch } from './cache';

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

const UNKNOWN_USER = 'Unknown User';

function normalizeDisplayName<T extends { displayName: string }>(data: T): T {
  if (!data.displayName) return { ...data, displayName: UNKNOWN_USER };
  return data;
}

export const api = {
  getSongs: () => cachedFetch('songs', () => get<SongsResponse>('/api/songs'), 5 * 60 * 1000),

  getLeaderboard: (songId: string, instrument: InstrumentKey, top = 100, offset = 0, leeway?: number) =>
    get<LeaderboardResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/${encodeURIComponent(instrument)}?top=${top}&offset=${offset}${leeway != null ? `&leeway=${leeway}` : ''}`,
    ),

  getPlayer: (accountId: string, songId?: string) =>
    get<PlayerResponse>(
      `/api/player/${encodeURIComponent(accountId)}${songId ? `?songId=${encodeURIComponent(songId)}` : ''}`,
    ).then(normalizeDisplayName),

  searchAccounts: (q: string, limit = 10) =>
    get<AccountSearchResponse>(
      `/api/account/search?q=${encodeURIComponent(q)}&limit=${limit}`,
    ),

  trackPlayer: (accountId: string) =>
    post<TrackPlayerResponse>(`/api/player/${encodeURIComponent(accountId)}/track`).then(normalizeDisplayName),

  getSyncStatus: (accountId: string) =>
    get<SyncStatusResponse>(`/api/player/${encodeURIComponent(accountId)}/sync-status`),

  getPlayerHistory: (accountId: string, songId?: string) =>
    get<PlayerHistoryResponse>(
      `/api/player/${encodeURIComponent(accountId)}/history${songId ? `?songId=${encodeURIComponent(songId)}` : ''}`,
    ),

  getAllLeaderboards: (songId: string, top = 10, leeway?: number) =>
    get<AllLeaderboardsResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/all?top=${top}${leeway != null ? `&leeway=${leeway}` : ''}`,
    ),

  getPlayerStats: (accountId: string) =>
    cachedFetch(`playerStats:${accountId}`, () =>
      get<PlayerStatsResponse>(`/api/player/${encodeURIComponent(accountId)}/stats`),
    5 * 60 * 1000),

  getVersion: () => cachedFetch('version', () => get<{ version: string }>('/api/version'), 30 * 60 * 1000),
};
