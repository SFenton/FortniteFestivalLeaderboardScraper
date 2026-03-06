import type {
  SongsResponse,
  LeaderboardResponse,
  PlayerResponse,
  AccountCheckResponse,
  AccountSearchResponse,
  ScrapeProgress,
  FirstSeenResponse,
  LeaderboardPopulationEntry,
  TrackPlayerResponse,
  SyncStatusResponse,
  InstrumentKey,
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

  getPlayer: (accountId: string) =>
    get<PlayerResponse>(`/api/player/${encodeURIComponent(accountId)}`),

  checkAccount: (username: string) =>
    get<AccountCheckResponse>(
      `/api/account/check?username=${encodeURIComponent(username)}`,
    ),

  searchAccounts: (q: string, limit = 10) =>
    get<AccountSearchResponse>(
      `/api/account/search?q=${encodeURIComponent(q)}&limit=${limit}`,
    ),

  getProgress: () => get<ScrapeProgress>('/api/progress'),

  getFirstSeen: () => get<FirstSeenResponse>('/api/firstseen'),

  getLeaderboardPopulation: () =>
    get<LeaderboardPopulationEntry[]>('/api/leaderboard-population'),

  trackPlayer: (accountId: string) =>
    post<TrackPlayerResponse>(`/api/player/${encodeURIComponent(accountId)}/track`),

  getSyncStatus: (accountId: string) =>
    get<SyncStatusResponse>(`/api/player/${encodeURIComponent(accountId)}/sync-status`),
};
