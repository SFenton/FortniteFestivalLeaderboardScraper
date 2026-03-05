import type {
  SongsResponse,
  LeaderboardResponse,
  PlayerResponse,
  AccountCheckResponse,
  ScrapeProgress,
  FirstSeenResponse,
  LeaderboardPopulationEntry,
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

  getProgress: () => get<ScrapeProgress>('/api/progress'),

  getFirstSeen: () => get<FirstSeenResponse>('/api/firstseen'),

  getLeaderboardPopulation: () =>
    get<LeaderboardPopulationEntry[]>('/api/leaderboard-population'),
};
