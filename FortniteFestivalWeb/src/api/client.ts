import type {
  SongsResponse,
  MemberScoreFilterResponse,
  LeaderboardResponse,
  LeaderboardRankOffsetsResponse,
  PlayerResponse,
  AccountNameRefreshResponse,
  AccountSearchResponse,
  TrackPlayerResponse,
  SyncStatusResponse,
  BandSyncStatusResponse,
  ServiceInfoResponse,
  PlayerHistoryResponse,
  ServerInstrumentKey as InstrumentKey,
  AllLeaderboardsResponse,
  SelectedMemberSongScoresResponse,
  AllSongBandLeaderboardsResponse,
  SongBandLeaderboardResponse,
  PlayerStatsResponse,
  PlayerBandListGroup,
  PlayerBandListResponse,
  PlayerBandTypeResponse,
  RivalsOverviewResponse,
  RivalsListResponse,
  RivalDetailResponse,
  RivalSuggestionsResponse,
  RivalsAllResponse,
  ShopResponse,
  RankingsPageResponse,
  AccountRankingDto,
  SelectedMemberRankingsResponse,
  CompositePageResponse,
  CompositeRankingDto,
  SoloFamilyScopeId,
  SoloFamilyPageResponse,
  SoloFamilyRankingDto,
  ComboPageResponse,
  ComboRankingEntry,
  BandDetailResponse,
  BandComboCatalogResponse,
  BandRankingDto,
  BandSearchRankBy,
  BandSearchResponse,
  BandRankHistoryResponse,
  BandRankingsPageResponse,
  BandRankingMetric,
  BandSongRowsResponse,
  BandSongsResponse,
  BandType,
  ImprovementNotificationsEnvelope,
  RankingMetric,
  LeaderboardNeighborhoodResponse,
  CompositeNeighborhoodResponse,
  LeaderboardRivalsListResponse,
  RankHistoryResponse,
  PublicationResponse,
} from '@festival/core/api';
import { expandWirePlayerResponse, expandWireSongsResponse, expandWireStatsResponse } from '@festival/core/api';
import { readSelectedProfile } from '../state/selectedProfile';
import {
  isSongsResponse,
  readSongsCache,
  writeSongsCache,
} from './songsCache';
import {
  ensurePublication,
  fetchWithPublication,
  getCurrentPublicationId,
} from './publication';

const BASE = '';
const SELECTED_PLAYER_HEADER = 'X-FST-Selected-Player';
const SELECTED_PROFILE_TYPE_HEADER = 'X-FST-Selected-Profile-Type';
const SELECTED_PROFILE_ID_HEADER = 'X-FST-Selected-Profile-Id';
const SELECTED_BAND_ID_HEADER = 'X-FST-Selected-Band-Id';
const SELECTED_BAND_TYPE_HEADER = 'X-FST-Selected-Band-Type';
const SELECTED_BAND_TEAM_KEY_HEADER = 'X-FST-Selected-Band-Team-Key';
const EXPORT_TIME_ZONE_HEADER = 'X-FST-Time-Zone';
const PUBLICATION_ID_HEADER = 'X-FST-Publication-Id';
const HTTP_NOT_MODIFIED = 304;

export type ApiRequestOptions = {
  signal?: AbortSignal;
};

function withSelectedProfileHeaders(headers: Record<string, string> = {}): Record<string, string> {
  try {
    const profile = readSelectedProfile();
    if (!profile) return headers;

    if (profile.type === 'player') {
      return {
        ...headers,
        [SELECTED_PROFILE_TYPE_HEADER]: 'player',
        [SELECTED_PROFILE_ID_HEADER]: profile.accountId,
        [SELECTED_PLAYER_HEADER]: profile.accountId,
      };
    }

    return {
      ...headers,
      [SELECTED_PROFILE_TYPE_HEADER]: 'band',
      [SELECTED_PROFILE_ID_HEADER]: profile.bandId,
      [SELECTED_BAND_ID_HEADER]: profile.bandId,
      [SELECTED_BAND_TYPE_HEADER]: profile.bandType,
      [SELECTED_BAND_TEAM_KEY_HEADER]: profile.teamKey,
    };
  } catch {
    return headers;
  }
}

async function get<T>(path: string, options?: ApiRequestOptions): Promise<T> {
  const init: RequestInit = { headers: withSelectedProfileHeaders() };
  if (options?.signal) init.signal = options.signal;

  const res = await fetchWithPublication(`${BASE}${path}`, init);
  if (!res.ok) {
    throw new Error(`API ${res.status}: ${res.statusText}`);
  }
  return res.json() as Promise<T>;
}

async function post<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetchWithPublication(`${BASE}${path}`, {
    method: 'POST',
    headers: withSelectedProfileHeaders({ 'Content-Type': 'application/json' }),
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    throw new Error(`API ${res.status}: ${res.statusText}`);
  }
  return res.json() as Promise<T>;
}

function getDownloadFileName(contentDisposition: string | null, fallback: string): string {
  if (!contentDisposition) return fallback;

  const encodedMatch = /filename\*=UTF-8''([^;]+)/i.exec(contentDisposition);
  if (encodedMatch?.[1]) {
    try {
      return decodeURIComponent(encodedMatch[1].replace(/"/g, '').trim());
    } catch {
      return encodedMatch[1].replace(/"/g, '').trim() || fallback;
    }
  }

  const quotedMatch = /filename="?([^";]+)"?/i.exec(contentDisposition);
  return quotedMatch?.[1]?.trim() || fallback;
}

function getBrowserTimeZone(): string | null {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || null;
  } catch {
    return null;
  }
}

async function download(path: string, fallbackFileName: string, headers: Record<string, string> = {}): Promise<void> {
  const res = await fetchWithPublication(`${BASE}${path}`, {
    headers: withSelectedProfileHeaders(headers),
    cache: 'no-store',
  });
  if (!res.ok) {
    throw new Error(`API ${res.status}: ${res.statusText}`);
  }

  const blob = await res.blob();
  const fileName = getDownloadFileName(res.headers.get('content-disposition'), fallbackFileName);
  const url = URL.createObjectURL(blob);
  try {
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
}

const UNKNOWN_USER = 'Unknown User';

const ALBUM_ART_PREFIX = 'https://cdn2.unrealengine.com/';

export function expandAlbumArt(songs: { albumArt?: string }[]): void {
  for (const song of songs) {
    if (song.albumArt && !song.albumArt.startsWith('http')) {
      song.albumArt = ALBUM_ART_PREFIX + song.albumArt;
    }
  }
}

function normalizeDisplayName<T extends { displayName: string }>(data: T): T {
  if (!data.displayName) return { ...data, displayName: UNKNOWN_USER };
  return data;
}

function getResponsePublicationId(response: Response): number | null {
  const raw = response.headers.get(PUBLICATION_ID_HEADER);
  if (raw != null) {
    const publicationId = Number(raw);
    if (Number.isSafeInteger(publicationId) && publicationId > 0) {
      return publicationId;
    }
    return null;
  }
  return getCurrentPublicationId();
}

export const api = {
  getPublication: (): Promise<PublicationResponse> => ensurePublication(),

  getSongs: async (options?: ApiRequestOptions): Promise<SongsResponse> => {
    const cached = readSongsCache();
    const headers: Record<string, string> = {};
    if (cached?.etag) headers['If-None-Match'] = cached.etag;

    // Bypass browser HTTP cache so our ETag check hits the server directly.
    // Without this, the browser's max-age (30 min) silently returns stale data.
    const init: RequestInit = { headers: withSelectedProfileHeaders(headers), cache: 'no-cache' };
    if (options?.signal) init.signal = options.signal;
    let res = await fetchWithPublication(`${BASE}/api/songs`, init);
    let responsePublicationId = getResponsePublicationId(res);

    // 304 Not Modified — server confirms our cached data is still current
    if (
      res.status === HTTP_NOT_MODIFIED
      && cached
      && cached.publicationId === responsePublicationId
      && responsePublicationId === getCurrentPublicationId()
    ) {
      return cached.data;
    }
    if (res.status === HTTP_NOT_MODIFIED) {
      const retryInit: RequestInit = {
        headers: withSelectedProfileHeaders(),
        cache: 'no-store',
      };
      if (options?.signal) retryInit.signal = options.signal;
      res = await fetchWithPublication(`${BASE}/api/songs`, retryInit);
      responsePublicationId = getResponsePublicationId(res);
    }

    if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);

    const data = expandWireSongsResponse(await res.json());
    if (!isSongsResponse(data)) throw new Error('Invalid songs response');
    expandAlbumArt(data.songs);
    if (
      responsePublicationId != null
      && responsePublicationId === getCurrentPublicationId()
    ) {
      writeSongsCache(data, res.headers.get('etag'), responsePublicationId);
    }
    return data;
  },

  getShop: async (options?: ApiRequestOptions): Promise<ShopResponse> => {
    const init: RequestInit = {
      headers: withSelectedProfileHeaders(),
      cache: 'no-cache',
    };
    if (options?.signal) init.signal = options.signal;
    const res = await fetchWithPublication(`${BASE}/api/shop`, init);
    if (!res.ok) throw new Error(`API ${res.status}: ${res.statusText}`);

    const data = await res.json() as ShopResponse;
    expandAlbumArt(data.songs);
    return data;
  },

  getMemberScoreFilter: (
    params: { hasAccountIds?: string[]; missingAccountIds?: string[]; instruments: InstrumentKey[]; leeway?: number },
    options?: ApiRequestOptions,
  ) => {
    const query = new URLSearchParams();
    if (params.hasAccountIds?.length) query.set('has', params.hasAccountIds.join(','));
    if (params.missingAccountIds?.length) query.set('missing', params.missingAccountIds.join(','));
    if (params.instruments.length) query.set('instruments', params.instruments.join(','));
    if (params.leeway != null) query.set('leeway', String(params.leeway));
    return get<MemberScoreFilterResponse>(`/api/songs/member-score-filter?${query.toString()}`, options);
  },

  getLeaderboard: (songId: string, instrument: InstrumentKey, top = 100, offset = 0, leeway?: number, options?: ApiRequestOptions) =>
    get<LeaderboardResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/${encodeURIComponent(instrument)}?top=${top}&offset=${offset}${leeway != null ? `&leeway=${leeway}` : ''}`,
      options,
    ),

  getLeaderboardRankOffsets: (songId: string, instrument: InstrumentKey, options?: ApiRequestOptions) =>
    get<LeaderboardRankOffsetsResponse>(
      `/api/leaderboard-rank-offsets/${encodeURIComponent(songId)}/${encodeURIComponent(instrument)}`,
      options,
    ),

  getPlayer: (accountId: string, songId?: string, instruments?: string[], leeway?: number, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    if (songId) params.set('songId', songId);
    if (instruments?.length) params.set('instruments', instruments.join(','));
    if (leeway != null) params.set('leeway', String(leeway));
    const qs = params.toString();
    return get<PlayerResponse>(
      `/api/player/${encodeURIComponent(accountId)}${qs ? `?${qs}` : ''}`,
      options,
    ).then(r => normalizeDisplayName(expandWirePlayerResponse(r as never)));
  },

  searchAccounts: (q: string, limit = 10, options?: ApiRequestOptions) =>
    get<AccountSearchResponse>(
      `/api/account/search?q=${encodeURIComponent(q)}&limit=${limit}`,
      options,
    ),

  refreshAccountNames: (accountIds: string[]) =>
    post<AccountNameRefreshResponse>('/api/account/name-refresh', { accountIds }),

  trackPlayer: (accountId: string) =>
    post<TrackPlayerResponse>(`/api/player/${encodeURIComponent(accountId)}/track`).then(normalizeDisplayName),

  getSyncStatus: (accountId: string, options?: ApiRequestOptions) =>
    get<SyncStatusResponse>(`/api/player/${encodeURIComponent(accountId)}/sync-status`, options),

  getBandSyncStatus: (bandType: BandType, teamKey: string, options?: ApiRequestOptions) =>
    get<BandSyncStatusResponse>(`/api/bands/${encodeURIComponent(bandType)}/${encodeURIComponent(teamKey)}/sync-status`, options),

  getPlayerNotifications: (accountId: string, limit = 50, options?: ApiRequestOptions) =>
    get<ImprovementNotificationsEnvelope>(`/api/player/${encodeURIComponent(accountId)}/notifications?limit=${limit}`, options),

  getBandNotificationsById: (bandId: string, limit = 50, options?: ApiRequestOptions) =>
    get<ImprovementNotificationsEnvelope>(`/api/bands/${encodeURIComponent(bandId)}/notifications?limit=${limit}`, options),

  getServiceInfo: async (signal?: AbortSignal): Promise<ServiceInfoResponse> => {
    const init: RequestInit = {
      cache: 'no-store',
      headers: { Accept: 'application/json' },
    };
    if (signal) init.signal = signal;

    const res = await fetchWithPublication(`${BASE}/api/service-info`, init);
    if (!res.ok) {
      throw new Error(`API ${res.status}: ${res.statusText}`);
    }
    return res.json() as Promise<ServiceInfoResponse>;
  },

  getPlayerHistory: (accountId: string, songId?: string, instrument?: string, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    if (songId) params.set('songId', songId);
    if (instrument) params.set('instrument', instrument);
    const qs = params.toString();
    return get<PlayerHistoryResponse>(
      `/api/player/${encodeURIComponent(accountId)}/history${qs ? `?${qs}` : ''}`,
      options,
    );
  },

  downloadPlayerExport: (accountId: string) => {
    const timeZone = getBrowserTimeZone();
    return download(
      `/api/player/${encodeURIComponent(accountId)}/export`,
      `fst-export-${accountId}.zip`,
      timeZone ? { [EXPORT_TIME_ZONE_HEADER]: timeZone } : {},
    );
  },

  downloadBandExport: (bandType: BandType, teamKey: string) => {
    const timeZone = getBrowserTimeZone();
    return download(
      `/api/bands/${encodeURIComponent(bandType)}/${encodeURIComponent(teamKey)}/export`,
      `fst-band-export-${bandType}-${teamKey}.zip`,
      timeZone ? { [EXPORT_TIME_ZONE_HEADER]: timeZone } : {},
    );
  },

  getAllLeaderboards: (songId: string, top = 10, leeway?: number, options?: ApiRequestOptions) =>
    get<AllLeaderboardsResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/all?top=${top}${leeway != null ? `&leeway=${leeway}` : ''}`,
      options,
    ),

  getSelectedMemberSongScores: (songId: string, accountIds: string[], instruments?: InstrumentKey[], leeway?: number, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    params.set('accountIds', accountIds.join(','));
    if (instruments?.length) params.set('instruments', instruments.join(','));
    if (leeway != null) params.set('leeway', String(leeway));
    return get<SelectedMemberSongScoresResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/members/scores?${params.toString()}`,
      options,
    );
  },

  getSongBandLeaderboard: (songId: string, bandType: BandType, top = 25, offset = 0, selectedAccountId?: string, selectedTeamKey?: string, comboId?: string, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    params.set('top', String(top));
    params.set('offset', String(offset));
    if (selectedAccountId) params.set('accountId', selectedAccountId);
    if (selectedTeamKey) params.set('teamKey', selectedTeamKey);
    if (comboId) params.set('combo', comboId);
    return get<SongBandLeaderboardResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/bands/${encodeURIComponent(bandType)}?${params.toString()}`,
      options,
    );
  },

  getAllSongBandLeaderboards: (songId: string, top = 10, selectedAccountId?: string, selectedBandType?: BandType, selectedTeamKey?: string, comboId?: string, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    params.set('top', String(top));
    if (selectedAccountId) params.set('accountId', selectedAccountId);
    if (selectedBandType) params.set('selectedBandType', selectedBandType);
    if (selectedTeamKey) params.set('selectedTeamKey', selectedTeamKey);
    if (comboId) params.set('combo', comboId);
    return get<AllSongBandLeaderboardsResponse>(
      `/api/leaderboard/${encodeURIComponent(songId)}/bands/all?${params.toString()}`,
      options,
    );
  },

  getPlayerStats: (accountId: string, options?: ApiRequestOptions) =>
    get<PlayerStatsResponse>(`/api/player/${encodeURIComponent(accountId)}/stats`, options)
      .then(r => expandWireStatsResponse(r as never)),

  getPlayerBandsByType: (accountId: string, bandType: BandType, comboId?: string, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    if (comboId) params.set('combo', comboId);
    const qs = params.toString();
    return get<PlayerBandTypeResponse>(
      `/api/player/${encodeURIComponent(accountId)}/bands/${encodeURIComponent(bandType)}${qs ? `?${qs}` : ''}`,
      options,
    );
  },

  getPlayerBandsList: (accountId: string, group: PlayerBandListGroup = 'all', page = 1, pageSize = 25, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    params.set('group', group);
    params.set('page', String(page));
    params.set('pageSize', String(pageSize));
    return get<PlayerBandListResponse>(
      `/api/player/${encodeURIComponent(accountId)}/bands?${params.toString()}`,
      options,
    );
  },

  getBandDetail: (bandId: string, options?: ApiRequestOptions) =>
    get<BandDetailResponse>(`/api/bands/${encodeURIComponent(bandId)}`, options),

  searchBands: (
    params: { q?: string; accountIds?: string[]; bandType?: BandType; combo?: string; rankBy?: BandSearchRankBy; page?: number; pageSize?: number },
    options?: ApiRequestOptions,
  ) => {
    const query = new URLSearchParams();
    if (params.q) query.set('q', params.q);
    if (params.accountIds?.length) query.set('accountIds', params.accountIds.join(','));
    if (params.bandType) query.set('bandType', params.bandType);
    if (params.combo) query.set('combo', params.combo);
    if (params.rankBy) query.set('rankBy', params.rankBy);
    if (params.page != null) query.set('page', String(params.page));
    if (params.pageSize != null) query.set('pageSize', String(params.pageSize));
    const qs = query.toString();
    return get<BandSearchResponse>(`/api/bands/search${qs ? `?${qs}` : ''}`, options);
  },

  getVersion: (options?: ApiRequestOptions) => get<{ version: string }>('/api/version', options),

  getRivalsOverview: (accountId: string, options?: ApiRequestOptions) =>
    get<RivalsOverviewResponse>(`/api/player/${encodeURIComponent(accountId)}/rivals`, options),

  getRivalsList: (accountId: string, combo: string, options?: ApiRequestOptions) =>
    get<RivalsListResponse>(
      `/api/player/${encodeURIComponent(accountId)}/rivals/${encodeURIComponent(combo)}`,
      options,
    ),

  getRivalDetail: (
    accountId: string,
    combo: string,
    rivalId: string,
    sort = 'closest',
    options?: { allowLiveFallback?: boolean; includeGaps?: boolean },
    requestOptions?: ApiRequestOptions,
  ) => {
    const params = new URLSearchParams();
    params.set('limit', '0');
    params.set('sort', sort);
    if (options?.allowLiveFallback) params.set('allowLiveFallback', 'true');
    if (options?.includeGaps) params.set('includeGaps', 'true');
    return get<RivalDetailResponse>(
      `/api/player/${encodeURIComponent(accountId)}/rivals/${encodeURIComponent(combo)}/${encodeURIComponent(rivalId)}?${params.toString()}`,
      requestOptions,
    );
  },

  // ─── Rankings ──────────────────────────────────────────────────

  getRankings: (
    instrument: InstrumentKey,
    rankBy: RankingMetric = 'totalscore',
    page = 1,
    pageSize = 10,
    options?: ApiRequestOptions & { leeway?: number },
  ) => {
    const { leeway, ...requestOptions } = options ?? {};
    return get<RankingsPageResponse>(
      `/api/rankings/${encodeURIComponent(instrument)}?rankBy=${encodeURIComponent(rankBy)}&page=${page}&pageSize=${pageSize}${leeway != null ? `&leeway=${leeway}` : ''}`,
      requestOptions,
    );
  },

  getPlayerRanking: (instrument: InstrumentKey, accountId: string, rankBy?: string, options?: ApiRequestOptions & { leeway?: number }) => {
    const { leeway, ...requestOptions } = options ?? {};
    const params = new URLSearchParams();
    if (rankBy) params.set('rankBy', rankBy);
    if (leeway != null) params.set('leeway', String(leeway));
    const query = params.toString();
    return get<AccountRankingDto>(
      `/api/rankings/${encodeURIComponent(instrument)}/${encodeURIComponent(accountId)}${query ? `?${query}` : ''}`,
      requestOptions,
    );
  },

  getSelectedMemberRankings: (accountIds: string[], instruments: InstrumentKey[], rankBy: RankingMetric = 'totalscore', options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    params.set('accountIds', accountIds.join(','));
    params.set('instruments', instruments.join(','));
    params.set('rankBy', rankBy);
    return get<SelectedMemberRankingsResponse>(
      `/api/rankings/selected-members?${params.toString()}`,
      options,
    );
  },

  getCompositeRankings: (page = 1, pageSize = 10, options?: ApiRequestOptions) =>
    get<CompositePageResponse>(
      `/api/rankings/composite?page=${page}&pageSize=${pageSize}`,
      options,
    ),

  getPlayerCompositeRanking: (accountId: string, options?: ApiRequestOptions) =>
    get<CompositeRankingDto>(
      `/api/rankings/composite/${encodeURIComponent(accountId)}`,
      options,
    ),

  getSoloFamilyRankings: (scopeId: SoloFamilyScopeId, rankBy: RankingMetric = 'totalscore', page = 1, pageSize = 10, options?: ApiRequestOptions) =>
    get<SoloFamilyPageResponse>(
      `/api/rankings/family/${encodeURIComponent(scopeId)}?rankBy=${encodeURIComponent(rankBy)}&page=${page}&pageSize=${pageSize}`,
      options,
    ),

  getPlayerSoloFamilyRanking: (accountId: string, scopeId: SoloFamilyScopeId, rankBy: RankingMetric = 'totalscore', options?: ApiRequestOptions) =>
    get<SoloFamilyRankingDto>(
      `/api/rankings/family/${encodeURIComponent(scopeId)}/${encodeURIComponent(accountId)}?rankBy=${encodeURIComponent(rankBy)}`,
      options,
    ),

  getComboRankings: (comboId: string, rankBy: RankingMetric = 'adjusted', page = 1, pageSize = 10, options?: ApiRequestOptions) =>
    get<ComboPageResponse>(
      `/api/rankings/combo?combo=${encodeURIComponent(comboId)}&rankBy=${encodeURIComponent(rankBy)}&page=${page}&pageSize=${pageSize}`,
      options,
    ),

  getPlayerComboRanking: (accountId: string, comboId: string, rankBy: RankingMetric = 'adjusted', options?: ApiRequestOptions) =>
    get<{ comboId: string; rankBy: string; totalAccounts: number } & ComboRankingEntry>(
      `/api/rankings/combo/${encodeURIComponent(accountId)}?combo=${encodeURIComponent(comboId)}&rankBy=${encodeURIComponent(rankBy)}`,
      options,
    ),

  getBandRankingCombos: (bandType: BandType, options?: ApiRequestOptions) =>
    get<BandComboCatalogResponse>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}/combos`,
      options,
    ),

  getBandRankings: (bandType: BandType, comboId?: string, rankBy: BandRankingMetric = 'adjusted', page = 1, pageSize = 10, selectedAccountId?: string, selectedTeamKey?: string, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    params.set('rankBy', rankBy);
    params.set('page', String(page));
    params.set('pageSize', String(pageSize));
    if (comboId) params.set('combo', comboId);
    if (selectedAccountId) params.set('accountId', selectedAccountId);
    if (selectedTeamKey) params.set('teamKey', selectedTeamKey);
    return get<BandRankingsPageResponse>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}?${params.toString()}`,
      options,
    );
  },

  getBandRanking: (bandType: BandType, teamKey: string, comboId?: string, rankBy: BandRankingMetric = 'adjusted', options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    params.set('rankBy', rankBy);
    if (comboId) params.set('combo', comboId);
    const qs = params.toString();
    return get<BandRankingDto>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}/${encodeURIComponent(teamKey)}${qs ? `?${qs}` : ''}`,
      options,
    );
  },

  getBandRankHistory: (bandType: BandType, teamKey: string, days?: number, comboId?: string, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    if (days != null) params.set('days', String(days));
    if (comboId) params.set('combo', comboId);
    const qs = params.toString();
    return get<BandRankHistoryResponse>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}/${encodeURIComponent(teamKey)}/history${qs ? `?${qs}` : ''}`,
      options,
    );
  },

  getBandSongs: (bandType: BandType, teamKey: string, limit = 5, comboId?: string, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    params.set('limit', String(limit));
    if (comboId) params.set('combo', comboId);
    return get<BandSongsResponse>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}/${encodeURIComponent(teamKey)}/songs?${params.toString()}`,
      options,
    );
  },

  getBandSongRows: (bandType: BandType, teamKey: string, comboId?: string, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    if (comboId) params.set('combo', comboId);
    const qs = params.toString();
    return get<BandSongRowsResponse>(
      `/api/rankings/bands/${encodeURIComponent(bandType)}/${encodeURIComponent(teamKey)}/song-rows${qs ? `?${qs}` : ''}`,
      options,
    );
  },

  getLeaderboardNeighborhood: (instrument: InstrumentKey, accountId: string, radius = 5, options?: ApiRequestOptions) =>
    get<LeaderboardNeighborhoodResponse>(
      `/api/rankings/${encodeURIComponent(instrument)}/${encodeURIComponent(accountId)}/neighborhood?radius=${radius}`,
      options,
    ),

  getCompositeNeighborhood: (accountId: string, radius = 5, options?: ApiRequestOptions) =>
    get<CompositeNeighborhoodResponse>(
      `/api/rankings/composite/${encodeURIComponent(accountId)}/neighborhood?radius=${radius}`,
      options,
    ),

  getLeaderboardRivals: (instrument: InstrumentKey, accountId: string, rankBy: RankingMetric = 'totalscore', options?: ApiRequestOptions) =>
    get<LeaderboardRivalsListResponse>(
      `/api/player/${encodeURIComponent(accountId)}/leaderboard-rivals/${encodeURIComponent(instrument)}?rankBy=${encodeURIComponent(rankBy)}`,
      options,
    ),

  getLeaderboardRivalDetail: (instrument: InstrumentKey, accountId: string, rivalId: string, rankBy: RankingMetric = 'totalscore', sort = 'closest', options?: ApiRequestOptions) =>
    get<RivalDetailResponse>(
      `/api/player/${encodeURIComponent(accountId)}/leaderboard-rivals/${encodeURIComponent(instrument)}/${encodeURIComponent(rivalId)}?rankBy=${encodeURIComponent(rankBy)}&sort=${encodeURIComponent(sort)}`,
      options,
    ),

  getRivalSuggestions: (accountId: string, combo?: string, limit = 5, options?: ApiRequestOptions) => {
    const params = new URLSearchParams();
    if (combo) params.set('combo', combo);
    params.set('limit', String(limit));
    return get<RivalSuggestionsResponse>(
      `/api/player/${encodeURIComponent(accountId)}/rivals/suggestions?${params}`,
      options,
    );
  },

  getRivalsAll: (accountId: string, options?: ApiRequestOptions) =>
    get<RivalsAllResponse>(
      `/api/player/${encodeURIComponent(accountId)}/rivals/all`,
      options,
    ),

  getRankHistory: (instrument: InstrumentKey, accountId: string, days?: number, options?: ApiRequestOptions & { leeway?: number }) => {
    const { leeway, ...requestOptions } = options ?? {};
    const params = new URLSearchParams();
    if (days != null) params.set('days', String(days));
    if (leeway != null) params.set('leeway', String(leeway));
    const qs = params.toString();
    return get<RankHistoryResponse>(
      `/api/rankings/${encodeURIComponent(instrument)}/${encodeURIComponent(accountId)}/history${qs ? `?${qs}` : ''}`,
      requestOptions,
    );
  },
};
