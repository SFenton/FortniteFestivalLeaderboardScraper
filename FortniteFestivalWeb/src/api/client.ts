import type {
  SongsResponse,
  LeaderboardResponse,
  PlayerResponse,
  AccountSearchResponse,
  TrackPlayerResponse,
  SyncStatusResponse,
  ServiceInfoResponse,
  PlayerHistoryResponse,
  ServerInstrumentKey as InstrumentKey,
  AllLeaderboardsResponse,
  PlayerStatsResponse,
  PlayerBandTypeResponse,
  RivalsOverviewResponse,
  RivalsListResponse,
  RivalDetailResponse,
  RivalSuggestionsResponse,
  RivalsAllResponse,
  ShopResponse,
  RankingsPageResponse,
  AccountRankingDto,
  CompositePageResponse,
  CompositeRankingDto,
  ComboPageResponse,
  ComboRankingEntry,
  BandComboCatalogResponse,
  BandRankingDto,
  BandRankingsPageResponse,
  BandRankingMetric,
  BandType,
  RankingMetric,
  LeaderboardNeighborhoodResponse,
  CompositeNeighborhoodResponse,
  LeaderboardRivalsListResponse,
  RankHistoryResponse,
} from '@festival/core/api/serverTypes';
import { expandWirePlayerResponse, expandWireSongsResponse, expandWireStatsResponse } from '@festival/core/api/serverTypes';

const BASE = '';
const TRACKED_PLAYER_STORAGE_KEY = 'fst:trackedPlayer';
const SELECTED_PLAYER_HEADER = 'X-FST-Selected-Player';

function withSelectedPlayerHeader(headers: Record<string, string> = {}): Record<string, string> {
  try {
    const raw = localStorage.getItem(TRACKED_PLAYER_STORAGE_KEY);
    if (!raw) return headers;

    const parsed = JSON.parse(raw) as { accountId?: string } | null;
    const accountId = parsed?.accountId?.trim();
    if (!accountId) return headers;

    return {
      ...headers,
      [SELECTED_PLAYER_HEADER]: accountId,
    };
  } catch {
    return headers;
  }
}

async function get<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { headers: withSelectedPlayerHeader() });
  if (!res.ok) {
    throw new Error(`API ${res.status}: ${res.statusText}`);
  }
  return res.json() as Promise<T>;
}

async function post<T>(path: string): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: 'POST',
    headers: withSelectedPlayerHeader({ 'Content-Type': 'application/json' }),
  });
  if (!res.ok) {
    throw new Error(`API ${res.status}: ${res.statusText}`);
  }
  return res.json() as Promise<T>;
}

const UNKNOWN_USER = 'Unknown User';

const ALBUM_ART_PREFIX = 'https://cdn2.unrealengine.com/';
const SONGS_CACHE_KEY = 'fst_songs_cache';
/** Bump when the SongsResponse shape changes (e.g. adding maxScores). */
const SONGS_CACHE_VERSION = 2;

export function expandAlbumArt(songs: { albumArt?: string }[]): void {
  for (const song of songs) {
    if (song.albumArt && !song.albumArt.startsWith('http')) {
      song.albumArt = ALBUM_ART_PREFIX + song.albumArt;
    }
  }
}

type SongsCache = { data: SongsResponse; etag: string | null; v?: number };

function loadSongsCache(): SongsCache | null {
  try {
    const raw = localStorage.getItem(SONGS_CACHE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as SongsCache;
    // Discard caches from before the current version (shape may have changed)
    if ((parsed.v ?? 0) < SONGS_CACHE_VERSION) {
      localStorage.removeItem(SONGS_CACHE_KEY);
      return null;
    }
    return parsed;
  } catch {
    return null;
  }
}

function saveSongsCache(data: SongsResponse, etag: string | null): void {
  try {
    localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({ data, etag, v: SONGS_CACHE_VERSION }));
  } catch { /* quota exceeded — ignore */ }
}

function normalizeDisplayName<T extends { displayName: string }>(data: T): T {
  if (!data.displayName) return { ...data, displayName: UNKNOWN_USER };
  return data;
}

// ── Generic ETag cache (in-memory, per-URL) ─────────────────

const etagCache = new Map<string, { data: unknown; etag: string }>();

async function getWithETag<T>(path: string): Promise<T> {
  const url = `${BASE}${path}`;
  const cached = etagCache.get(url);
  const headers: Record<string, string> = {};
  if (cached?.etag) headers['If-None-Match'] = cached.etag;

  const res = await fetch(url, { headers: withSelectedPlayerHeader(headers) });

  if (res.status === 304 && cached) return cached.data as T;
  if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);

  const data = await res.json() as T;
  const etag = res.headers.get('etag');
  if (etag) etagCache.set(url, { data, etag });
  return data;
}

export const api = {
  getSongs: async (): Promise<SongsResponse> => {
    const cached = loadSongsCache();
    const headers: Record<string, string> = {};
    if (cached?.etag) headers['If-None-Match'] = cached.etag;

    // Bypass browser HTTP cache so our ETag check hits the server directly.
    // Without this, the browser's max-age (30 min) silently returns stale data.
    const res = await fetch(`${BASE}/api/songs`, { headers: withSelectedPlayerHeader(headers), cache: 'no-cache' });

    // 304 Not Modified — server confirms our cached data is still current
    if (res.status === 304 && cached) return cached.data;

    if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);

    const data = expandWireSongsResponse(await res.json());
    expandAlbumArt(data.songs);
    saveSongsCache(data, res.headers.get('etag'));
    return data;
  },

  getShop: async (): Promise<ShopResponse> => {
    const data = await getWithETag<ShopResponse>('/api/shop');
    expandAlbumArt(data.songs);
    return data;
  },

  getLeaderboard: (songId: string, instrument: InstrumentKey, top = 100, offset = 0, leeway?: number) =>
    get<LeaderboardResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/${encodeURIComponent(instrument)}?top=${top}&offset=${offset}${leeway != null ? `&leeway=${leeway}` : ''}`,
    ),

  getPlayer: (accountId: string, songId?: string, instruments?: string[], leeway?: number) => {
    const params = new URLSearchParams();
    if (songId) params.set('songId', songId);
    if (instruments?.length) params.set('instruments', instruments.join(','));
    if (leeway != null) params.set('leeway', String(leeway));
    const qs = params.toString();
    return getWithETag<PlayerResponse>(
      `/api/player/${encodeURIComponent(accountId)}${qs ? `?${qs}` : ''}`,
    ).then(r => normalizeDisplayName(expandWirePlayerResponse(r as never)));
  },

  searchAccounts: (q: string, limit = 10) =>
    get<AccountSearchResponse>(
      `/api/account/search?q=${encodeURIComponent(q)}&limit=${limit}`,
    ),

  trackPlayer: (accountId: string) =>
    post<TrackPlayerResponse>(`/api/player/${encodeURIComponent(accountId)}/track`).then(normalizeDisplayName),

  getSyncStatus: (accountId: string) =>
    get<SyncStatusResponse>(`/api/player/${encodeURIComponent(accountId)}/sync-status`),

  getServiceInfo: () =>
    get<ServiceInfoResponse>('/api/service-info'),

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
    getWithETag<AllLeaderboardsResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/all?top=${top}${leeway != null ? `&leeway=${leeway}` : ''}`,
    ),

  getPlayerStats: (accountId: string) =>
    get<PlayerStatsResponse>(`/api/player/${encodeURIComponent(accountId)}/stats`)
      .then(r => expandWireStatsResponse(r as never)),

  getPlayerBandsByType: (accountId: string, bandType: BandType, comboId?: string) => {
    const params = new URLSearchParams();
    if (comboId) params.set('combo', comboId);
    const qs = params.toString();
    return getWithETag<PlayerBandTypeResponse>(
      `/api/player/${encodeURIComponent(accountId)}/bands/${encodeURIComponent(bandType)}${qs ? `?${qs}` : ''}`,
    );
  },

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

  getPlayerRanking: (instrument: InstrumentKey, accountId: string, rankBy?: string) => {
    const params = rankBy ? `rankBy=${encodeURIComponent(rankBy)}` : '';
    return get<AccountRankingDto>(
      `/api/rankings/${encodeURIComponent(instrument)}/${encodeURIComponent(accountId)}${params ? `?${params}` : ''}`,
    );
  },

  getCompositeRankings: (page = 1, pageSize = 10) =>
    get<CompositePageResponse>(
      `/api/rankings/composite?page=${page}&pageSize=${pageSize}`,
    ),

  getPlayerCompositeRanking: (accountId: string) =>
    get<CompositeRankingDto>(
      `/api/rankings/composite/${encodeURIComponent(accountId)}`,
    ),

  getComboRankings: (comboId: string, rankBy: RankingMetric = 'adjusted', page = 1, pageSize = 10) =>
    get<ComboPageResponse>(
      `/api/rankings/combo?combo=${encodeURIComponent(comboId)}&rankBy=${encodeURIComponent(rankBy)}&page=${page}&pageSize=${pageSize}`,
    ),

  getPlayerComboRanking: (accountId: string, comboId: string, rankBy: RankingMetric = 'adjusted') =>
    get<{ comboId: string; rankBy: string; totalAccounts: number } & ComboRankingEntry>(
      `/api/rankings/combo/${encodeURIComponent(accountId)}?combo=${encodeURIComponent(comboId)}&rankBy=${encodeURIComponent(rankBy)}`,
    ),

  getBandRankingCombos: (bandType: BandType) =>
    get<BandComboCatalogResponse>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}/combos`,
    ),

  getBandRankings: (bandType: BandType, comboId?: string, rankBy: BandRankingMetric = 'adjusted', page = 1, pageSize = 10) => {
    const params = new URLSearchParams();
    params.set('rankBy', rankBy);
    params.set('page', String(page));
    params.set('pageSize', String(pageSize));
    if (comboId) params.set('combo', comboId);
    return get<BandRankingsPageResponse>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}?${params.toString()}`,
    );
  },

  getBandRanking: (bandType: BandType, teamKey: string, comboId?: string, rankBy: BandRankingMetric = 'adjusted') => {
    const params = new URLSearchParams();
    params.set('rankBy', rankBy);
    if (comboId) params.set('combo', comboId);
    const qs = params.toString();
    return get<BandRankingDto>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}/${encodeURIComponent(teamKey)}${qs ? `?${qs}` : ''}`,
    );
  },

  getLeaderboardNeighborhood: (instrument: InstrumentKey, accountId: string, radius = 5) =>
    getWithETag<LeaderboardNeighborhoodResponse>(
      `/api/rankings/${encodeURIComponent(instrument)}/${encodeURIComponent(accountId)}/neighborhood?radius=${radius}`,
    ),

  getCompositeNeighborhood: (accountId: string, radius = 5) =>
    getWithETag<CompositeNeighborhoodResponse>(
      `/api/rankings/composite/${encodeURIComponent(accountId)}/neighborhood?radius=${radius}`,
    ),

  getLeaderboardRivals: (instrument: InstrumentKey, accountId: string, rankBy: RankingMetric = 'totalscore') =>
    getWithETag<LeaderboardRivalsListResponse>(
      `/api/player/${encodeURIComponent(accountId)}/leaderboard-rivals/${encodeURIComponent(instrument)}?rankBy=${encodeURIComponent(rankBy)}`,
    ),

  getLeaderboardRivalDetail: (instrument: InstrumentKey, accountId: string, rivalId: string, rankBy: RankingMetric = 'totalscore', sort = 'closest') =>
    getWithETag<RivalDetailResponse>(
      `/api/player/${encodeURIComponent(accountId)}/leaderboard-rivals/${encodeURIComponent(instrument)}/${encodeURIComponent(rivalId)}?rankBy=${encodeURIComponent(rankBy)}&sort=${encodeURIComponent(sort)}`,
    ),

  getRivalSuggestions: (accountId: string, combo?: string, limit = 5) => {
    const params = new URLSearchParams();
    if (combo) params.set('combo', combo);
    params.set('limit', String(limit));
    return getWithETag<RivalSuggestionsResponse>(
      `/api/player/${encodeURIComponent(accountId)}/rivals/suggestions?${params}`,
    );
  },

  getRivalsAll: (accountId: string) =>
    getWithETag<RivalsAllResponse>(
      `/api/player/${encodeURIComponent(accountId)}/rivals/all`,
    ),

  getRankHistory: (instrument: InstrumentKey, accountId: string, days?: number) => {
    const params = new URLSearchParams();
    if (days != null) params.set('days', String(days));
    const qs = params.toString();
    return get<RankHistoryResponse>(
      `/api/rankings/${encodeURIComponent(instrument)}/${encodeURIComponent(accountId)}/history${qs ? `?${qs}` : ''}`,
    );
  },
};
