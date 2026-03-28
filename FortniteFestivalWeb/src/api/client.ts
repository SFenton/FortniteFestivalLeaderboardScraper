import type {
  SongsResponse,
  LeaderboardResponse,
  PlayerResponse,
  AccountSearchResponse,
  TrackPlayerResponse,
  SyncStatusResponse,
  PlayerHistoryResponse,
  ServerInstrumentKey as InstrumentKey,
  AllLeaderboardsResponse,
  PlayerStatsResponse,
  RivalsOverviewResponse,
  RivalsListResponse,
  RivalDetailResponse,
  RankingsPageResponse,
  AccountRankingDto,
  CompositePageResponse,
  CompositeRankingDto,
  ComboPageResponse,
  RankingMetric,
} from '@festival/core/api/serverTypes';

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
  getSongs: () => get<SongsResponse>('/api/songs'),

  getLeaderboard: (songId: string, instrument: InstrumentKey, top = 100, offset = 0, leeway?: number) =>
    get<LeaderboardResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/${encodeURIComponent(instrument)}?top=${top}&offset=${offset}${leeway != null ? `&leeway=${leeway}` : ''}`,
    ),

  getPlayer: (accountId: string, songId?: string, instruments?: string[]) => {
    const params = new URLSearchParams();
    if (songId) params.set('songId', songId);
    if (instruments?.length) params.set('instruments', instruments.join(','));
    const qs = params.toString();
    return get<PlayerResponse>(
      `/api/player/${encodeURIComponent(accountId)}${qs ? `?${qs}` : ''}`,
    ).then(normalizeDisplayName);
  },

  searchAccounts: (q: string, limit = 10) =>
    get<AccountSearchResponse>(
      `/api/account/search?q=${encodeURIComponent(q)}&limit=${limit}`,
    ),

  trackPlayer: (accountId: string) =>
    post<TrackPlayerResponse>(`/api/player/${encodeURIComponent(accountId)}/track`).then(normalizeDisplayName),

  getSyncStatus: (accountId: string) =>
    get<SyncStatusResponse>(`/api/player/${encodeURIComponent(accountId)}/sync-status`),

  getPlayerHistory: (accountId: string, songId?: string, instrument?: string) => {
    const params = new URLSearchParams();
    if (songId) params.set('songId', songId);
    if (instrument) params.set('instrument', instrument);
    const qs = params.toString();
    return get<PlayerHistoryResponse>(
      `/api/player/${encodeURIComponent(accountId)}/history${qs ? `?${qs}` : ''}`,
    );
  },

  getAllLeaderboards: (songId: string, top = 10, leeway?: number) =>
    get<AllLeaderboardsResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/all?top=${top}${leeway != null ? `&leeway=${leeway}` : ''}`,
    ),

  getPlayerStats: (accountId: string) =>
    get<PlayerStatsResponse>(`/api/player/${encodeURIComponent(accountId)}/stats`),

  getVersion: () => get<{ version: string }>('/api/version'),

  getRivalsOverview: (accountId: string) =>
    get<RivalsOverviewResponse>(`/api/player/${encodeURIComponent(accountId)}/rivals`),

  getRivalsList: (accountId: string, combo: string) =>
    get<RivalsListResponse>(
      `/api/player/${encodeURIComponent(accountId)}/rivals/${encodeURIComponent(combo)}`,
    ),

  getRivalDetail: (accountId: string, combo: string, rivalId: string, sort = 'closest') =>
    get<RivalDetailResponse>(
      `/api/player/${encodeURIComponent(accountId)}/rivals/${encodeURIComponent(combo)}/${encodeURIComponent(rivalId)}?limit=0&sort=${encodeURIComponent(sort)}`,
    ),

  // ─── Rankings ──────────────────────────────────────────────────

  getRankings: (instrument: InstrumentKey, rankBy: RankingMetric = 'totalscore', page = 1, pageSize = 10) =>
    get<RankingsPageResponse>(
      `/api/rankings/${encodeURIComponent(instrument)}?rankBy=${encodeURIComponent(rankBy)}&page=${page}&pageSize=${pageSize}`,
    ),

  getPlayerRanking: (instrument: InstrumentKey, accountId: string) =>
    get<AccountRankingDto>(
      `/api/rankings/${encodeURIComponent(instrument)}/${encodeURIComponent(accountId)}`,
    ),

  getCompositeRankings: (page = 1, pageSize = 10) =>
    get<CompositePageResponse>(
      `/api/rankings/composite?page=${page}&pageSize=${pageSize}`,
    ),

  getPlayerCompositeRanking: (accountId: string) =>
    get<CompositeRankingDto>(
      `/api/rankings/composite/${encodeURIComponent(accountId)}`,
    ),

  getComboRankings: (instruments: string, page = 1, pageSize = 10) =>
    get<ComboPageResponse>(
      `/api/rankings/combo?instruments=${encodeURIComponent(instruments)}&page=${page}&pageSize=${pageSize}`,
    ),
};
