import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api, expandAlbumArt } from '../../src/api/client';
import {
  clearSongsCache,
  PUBLIC_CATALOG_CACHE_SCOPE,
  SONGS_CACHE_KEY,
  SONGS_CACHE_VERSION,
} from '../../src/api/songsCache';
import { setPublicationForTests } from '../../src/api/publication';

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearSongsCache();
  setPublicationForTests(42, false);
  global.fetch = vi.fn();
});

function mockFetchOk(data: unknown) {
  (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
    ok: true,
    status: 200,
    json: () => Promise.resolve(data),
    headers: new Headers(),
  });
}

function mockFetchError(status: number, statusText: string) {
  (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
    ok: false,
    status,
    statusText,
  });
}

function jsonResponse(
  data: unknown,
  status = 200,
  headers?: HeadersInit,
): Response {
  const responseHeaders = new Headers(headers);
  if (!responseHeaders.has('Content-Type')) {
    responseHeaders.set('Content-Type', 'application/json');
  }
  return new Response(JSON.stringify(data), {
    status,
    headers: responseHeaders,
  });
}

describe('api/client', () => {
  describe('getServiceInfo', () => {
    it('uses an abortable no-store reachability request without profile headers', async () => {
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'tracked-1', displayName: 'Tracked' }));
      const data = { currentUpdate: { status: 'failed' }, workerStatus: { status: 'offline' } };
      const controller = new AbortController();
      mockFetchOk(data);

      const result = await api.getServiceInfo(controller.signal);

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/service-info', {
        cache: 'no-store',
        headers: { Accept: 'application/json' },
        signal: controller.signal,
      });
    });

    it('propagates malformed JSON as an availability failure', async () => {
      (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
        ok: true,
        status: 200,
        statusText: 'OK',
        json: () => Promise.reject(new SyntaxError('Unexpected token')),
      });

      await expect(api.getServiceInfo()).rejects.toThrow(SyntaxError);
    });
  });

  describe('refreshAccountNames', () => {
    it('posts account IDs to the silent refresh endpoint', async () => {
      const data = { changed: 0, unchanged: 2, failed: 0, missing: 0, names: {}, changedAccountIds: [] };
      mockFetchOk(data);

      const result = await api.refreshAccountNames(['acct1', 'acct2']);

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/account/name-refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ accountIds: ['acct1', 'acct2'] }),
      });
    });
  });

  describe('getSongs', () => {
    it('fetches songs from /api/songs', async () => {
      const data = { songs: [{ songId: 's1', title: 'Test', artist: 'Artist' }], count: 1, currentSeason: 5 };
      mockFetchOk(data);
      const result = await api.getSongs();
      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/songs', { headers: {}, cache: 'no-cache' });
    });

    it('sends If-None-Match when cached ETag exists', async () => {
      // Seed localStorage with a cached response + etag
      const cached = { songs: [{ songId: 's1', title: 'Old', artist: 'Artist' }], count: 1, currentSeason: 5 };
      localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
        version: SONGS_CACHE_VERSION,
        scope: PUBLIC_CATALOG_CACHE_SCOPE,
        publicationId: 42,
        data: cached,
        etag: '"abc123"',
      }));

      (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
        ok: false,
        status: 304,
        headers: new Headers(),
      });

      const result = await api.getSongs();
      expect(global.fetch).toHaveBeenCalledWith('/api/songs', {
        headers: { 'If-None-Match': '"abc123"' },
        cache: 'no-cache',
      });
      expect(result).toEqual(cached);
    });

    it('retries without browser cache when 304 has no application cache body', async () => {
      const data = { songs: [{ songId: 's1', title: 'Test', artist: 'Artist' }], count: 1, currentSeason: 5 };
      (global.fetch as ReturnType<typeof vi.fn>)
        .mockResolvedValueOnce({
          ok: false,
          status: 304,
          headers: new Headers(),
        })
        .mockResolvedValueOnce({
          ok: true,
          status: 200,
          json: () => Promise.resolve(data),
          headers: new Headers({ etag: '"fresh"' }),
        });

      await expect(api.getSongs()).resolves.toEqual(data);
      expect(global.fetch).toHaveBeenNthCalledWith(1, '/api/songs', {
        headers: {},
        cache: 'no-cache',
      });
      expect(global.fetch).toHaveBeenNthCalledWith(2, '/api/songs', {
        headers: {},
        cache: 'no-store',
      });
    });

    it('updates cache on 200 with new ETag', async () => {
      const data = { songs: [{ songId: 's2', title: 'New', artist: 'Artist' }], count: 1, currentSeason: 6 };
      (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
        ok: true,
        status: 200,
        json: () => Promise.resolve(data),
        headers: new Headers({ etag: '"newetag"' }),
      });

      await api.getSongs();
      const stored = JSON.parse(localStorage.getItem(SONGS_CACHE_KEY)!);
      expect(stored.etag).toBe('"newetag"');
      expect(stored.version).toBe(SONGS_CACHE_VERSION);
      expect(stored.scope).toBe(PUBLIC_CATALOG_CACHE_SCOPE);
      expect(stored.publicationId).toBe(42);
      expect(stored.data.songs[0].songId).toBe('s2');
    });

    it('preserves ETag and no-cache semantics when a caller supplies a signal', async () => {
      const cached = { songs: [{ songId: 's1', title: 'Old', artist: 'Artist' }], count: 1, currentSeason: 5 };
      localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
        version: SONGS_CACHE_VERSION,
        scope: PUBLIC_CATALOG_CACHE_SCOPE,
        publicationId: 42,
        data: cached,
        etag: '"abc123"',
      }));
      const controller = new AbortController();
      mockFetchOk({ songs: [], count: 0, currentSeason: 5 });

      await api.getSongs({ signal: controller.signal });

      expect(global.fetch).toHaveBeenCalledWith('/api/songs', {
        headers: { 'If-None-Match': '"abc123"' },
        cache: 'no-cache',
        signal: controller.signal,
      });
    });

    it('safely reuses the public ETag cache across selected profiles', async () => {
      const cached = {
        songs: [{ songId: 's1', title: 'Shared', artist: 'Artist' }],
        count: 1,
        currentSeason: 5,
      };
      localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
        version: SONGS_CACHE_VERSION,
        scope: PUBLIC_CATALOG_CACHE_SCOPE,
        publicationId: 42,
        data: cached,
        etag: '"shared-etag"',
      }));
      localStorage.setItem('fst:selectedProfile', JSON.stringify({
        type: 'player',
        accountId: 'player-1',
        displayName: 'Player One',
      }));
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({
        accountId: 'player-1',
        displayName: 'Player One',
      }));
      (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
        ok: false,
        status: 304,
        headers: new Headers({ etag: '"shared-etag"' }),
      });

      await expect(api.getSongs()).resolves.toEqual(cached);
      expect(global.fetch).toHaveBeenLastCalledWith('/api/songs', {
        cache: 'no-cache',
        headers: {
          'If-None-Match': '"shared-etag"',
          'X-FST-Selected-Profile-Type': 'player',
          'X-FST-Selected-Profile-Id': 'player-1',
          'X-FST-Selected-Player': 'player-1',
        },
      });

      localStorage.setItem('fst:selectedProfile', JSON.stringify({
        type: 'band',
        bandId: 'band-1',
        bandType: 'Band_Duets',
        teamKey: 'player-1:player-2',
        displayName: 'Band One',
        members: [],
      }));
      await expect(api.getSongs()).resolves.toEqual(cached);
      expect(global.fetch).toHaveBeenLastCalledWith('/api/songs', {
        cache: 'no-cache',
        headers: {
          'If-None-Match': '"shared-etag"',
          'X-FST-Selected-Profile-Type': 'band',
          'X-FST-Selected-Profile-Id': 'band-1',
          'X-FST-Selected-Band-Id': 'band-1',
          'X-FST-Selected-Band-Type': 'Band_Duets',
          'X-FST-Selected-Band-Team-Key': 'player-1:player-2',
        },
      });
    });

    it('does not reuse an ETag or body from a different publication', async () => {
      const stale = {
        songs: [{ songId: 'old', title: 'Old', artist: 'Artist' }],
        count: 1,
        currentSeason: 5,
      };
      const fresh = {
        songs: [{ songId: 'new', title: 'New', artist: 'Artist' }],
        count: 1,
        currentSeason: 6,
      };
      localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
        version: SONGS_CACHE_VERSION,
        scope: PUBLIC_CATALOG_CACHE_SCOPE,
        publicationId: 41,
        data: stale,
        etag: '"stale-etag"',
      }));
      (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue(
        jsonResponse(fresh, 200, {
          etag: '"fresh-etag"',
          'X-FST-Publication-Id': '42',
        }),
      );

      await expect(api.getSongs()).resolves.toEqual(fresh);
      expect(global.fetch).toHaveBeenCalledWith('/api/songs', {
        headers: {},
        cache: 'no-cache',
      });
      expect(JSON.parse(localStorage.getItem(SONGS_CACHE_KEY)!)).toMatchObject({
        publicationId: 42,
        etag: '"fresh-etag"',
        data: fresh,
      });
    });

    it('falls back to a full body when publication changes before a 304', async () => {
      const stale = {
        songs: [{ songId: 'old', title: 'Old', artist: 'Artist' }],
        count: 1,
        currentSeason: 5,
      };
      const fresh = {
        songs: [{ songId: 'new', title: 'New', artist: 'Artist' }],
        count: 1,
        currentSeason: 6,
      };
      localStorage.setItem(SONGS_CACHE_KEY, JSON.stringify({
        version: SONGS_CACHE_VERSION,
        scope: PUBLIC_CATALOG_CACHE_SCOPE,
        publicationId: 42,
        data: stale,
        etag: '"stale-etag"',
      }));
      (global.fetch as ReturnType<typeof vi.fn>)
        .mockResolvedValueOnce(jsonResponse({
          status: 'publication_changed',
          currentPublicationId: 43,
        }, 409))
        .mockResolvedValueOnce(jsonResponse({
          publicationId: 43,
          previousPublicationId: 42,
          publishedScrapeId: 1277,
          publishedAt: '2026-08-03T02:00:00Z',
          pinningEnabled: false,
        }))
        .mockResolvedValueOnce(new Response(null, {
          status: 304,
          headers: { 'X-FST-Publication-Id': '43' },
        }))
        .mockResolvedValueOnce(jsonResponse(fresh, 200, {
          etag: '"fresh-etag"',
          'X-FST-Publication-Id': '43',
        }));

      await expect(api.getSongs()).resolves.toEqual(fresh);
      expect(global.fetch).toHaveBeenNthCalledWith(3, '/api/songs', {
        headers: { 'If-None-Match': '"stale-etag"' },
        cache: 'no-cache',
      });
      expect(global.fetch).toHaveBeenNthCalledWith(4, '/api/songs', {
        headers: {},
        cache: 'no-store',
      });
      expect(JSON.parse(localStorage.getItem(SONGS_CACHE_KEY)!)).toMatchObject({
        publicationId: 43,
        etag: '"fresh-etag"',
        data: fresh,
      });
    });
  });

  describe('getShop', () => {
    it('uses browser ETag revalidation and caller cancellation without a second data owner', async () => {
      const controller = new AbortController();
      mockFetchOk({
        songs: [{
          songId: 'shop-1',
          title: 'Shop Song',
          artist: 'Artist',
          shopUrl: 'https://shop/1',
        }],
      });

      const result = await api.getShop({ signal: controller.signal });

      expect(result.songs[0]?.songId).toBe('shop-1');
      expect(global.fetch).toHaveBeenCalledWith('/api/shop', {
        cache: 'no-cache',
        headers: {},
        signal: controller.signal,
      });
    });
  });

  describe('getLeaderboard', () => {
    it('fetches leaderboard with correct URL params', async () => {
      const data = { songId: 's1', instrument: 'Solo_Guitar', showLeaderboardEntryTotals: true, count: 0, totalEntries: 0, localEntries: 0, entries: [] };
      mockFetchOk(data);
      const result = await api.getLeaderboard('s1', 'Solo_Guitar' as any, 50, 10);
      expect(result.showLeaderboardEntryTotals).toBe(true);
      expect(global.fetch).toHaveBeenCalledWith('/api/leaderboard/s1/Solo_Guitar?top=50&offset=10', { headers: {} });
    });

    it('includes leeway param when provided', async () => {
      mockFetchOk({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
      await api.getLeaderboard('s1', 'Solo_Guitar' as any, 100, 0, 1.5);
      expect(global.fetch).toHaveBeenCalledWith('/api/leaderboard/s1/Solo_Guitar?top=100&offset=0&leeway=1.5', { headers: {} });
    });

    it('fetches leaderboard rank offsets', async () => {
      mockFetchOk({ songId: 's1', instrument: 'Solo_Guitar', maxScore: 100000, minLeewayTenths: -50, maxLeewayTenths: 50, stepTenths: 1, removed: [], exact: [] });
      await api.getLeaderboardRankOffsets('s1', 'Solo_Guitar' as any);
      expect(global.fetch).toHaveBeenCalledWith('/api/leaderboard-rank-offsets/s1/Solo_Guitar', { headers: {} });
    });
  });

  describe('getMemberScoreFilter', () => {
    it('fetches member score filter song IDs with all params', async () => {
      const data = { count: 1, songIds: ['s1'] };
      mockFetchOk(data);

      const result = await api.getMemberScoreFilter({
        hasAccountIds: ['acct-1'],
        missingAccountIds: ['acct-2'],
        instruments: ['Solo_Guitar' as any, 'Solo_Bass' as any],
        leeway: 1.5,
      });

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/songs/member-score-filter?has=acct-1&missing=acct-2&instruments=Solo_Guitar%2CSolo_Bass&leeway=1.5', { headers: {} });
    });
  });

  describe('song band leaderboards', () => {
    it('fetches a single song band leaderboard with selected context and combo', async () => {
      const data = { songId: 's1', bandType: 'Band_Duets', showLeaderboardEntryTotals: true, count: 0, totalEntries: 0, localEntries: 0, entries: [] };
      mockFetchOk(data);

      const result = await api.getSongBandLeaderboard('s1', 'Band_Duets', 25, 50, 'acct-1', 'acct-1:acct-2', 'Solo_Guitar+Solo_Bass');

      expect(result).toEqual(data);
      expect(result.showLeaderboardEntryTotals).toBe(true);
      expect(global.fetch).toHaveBeenCalledWith('/api/leaderboard/s1/bands/Band_Duets?top=25&offset=50&accountId=acct-1&teamKey=acct-1%3Aacct-2&combo=Solo_Guitar%2BSolo_Bass', { headers: {} });
    });

    it('fetches all song band leaderboards with selected band type and combo', async () => {
      const data = { songId: 's1', bands: [] };
      mockFetchOk(data);

      const result = await api.getAllSongBandLeaderboards('s1', 10, undefined, 'Band_Duets', 'acct-1:acct-2', 'Solo_Guitar+Solo_Bass');

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/leaderboard/s1/bands/all?top=10&selectedBandType=Band_Duets&selectedTeamKey=acct-1%3Aacct-2&combo=Solo_Guitar%2BSolo_Bass', { headers: {} });
    });
  });

  describe('getPlayer', () => {
    it('fetches player with accountId', async () => {
      mockFetchOk({ accountId: 'p1', displayName: 'Player', totalScores: 0, scores: [] });
      const result = await api.getPlayer('p1');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1', { headers: {} });
      expect(result.displayName).toBe('Player');
    });

    it('includes songId and instruments query params', async () => {
      mockFetchOk({ accountId: 'p1', displayName: 'Player', totalScores: 0, scores: [] });
      await api.getPlayer('p1', 's1', ['Solo_Guitar', 'Solo_Bass']);
      expect(global.fetch).toHaveBeenCalledWith(expect.stringContaining('songId=s1'), expect.any(Object));
      expect(global.fetch).toHaveBeenCalledWith(expect.stringContaining('instruments=Solo_Guitar%2CSolo_Bass'), expect.any(Object));
    });

    it('normalizes empty displayName to Unknown User', async () => {
      mockFetchOk({ accountId: 'p1', displayName: '', totalScores: 0, scores: [] });
      const result = await api.getPlayer('p1');
      expect(result.displayName).toBe('Unknown User');
    });
  });

  describe('searchAccounts', () => {
    it('searches with query and limit', async () => {
      mockFetchOk({ results: [{ accountId: 'p1', displayName: 'Test' }] });
      await api.searchAccounts('test', 5);
      expect(global.fetch).toHaveBeenCalledWith('/api/account/search?q=test&limit=5', { headers: {} });
    });
  });

  describe('selected-profile headers', () => {
    it('includes player profile headers on GET requests when a tracked player is selected', async () => {
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'tracked-1', displayName: 'Tracked' }));
      mockFetchOk({ version: '1.0.0' });

      await api.getVersion();

      expect(global.fetch).toHaveBeenCalledWith('/api/version', {
        headers: {
          'X-FST-Selected-Profile-Type': 'player',
          'X-FST-Selected-Profile-Id': 'tracked-1',
          'X-FST-Selected-Player': 'tracked-1',
        },
      });
    });

    it('includes player profile headers on POST requests when a tracked player is selected', async () => {
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'tracked-1', displayName: 'Tracked' }));
      mockFetchOk({ accountId: 'p1', displayName: 'Player', trackingStarted: true, backfillStatus: 'queued' });

      await api.trackPlayer('p1');

      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/track', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-FST-Selected-Profile-Type': 'player',
          'X-FST-Selected-Profile-Id': 'tracked-1',
          'X-FST-Selected-Player': 'tracked-1',
        },
      });
    });

    it('includes band profile headers without a stale player header when a band is selected', async () => {
      localStorage.setItem('fst:selectedProfile', JSON.stringify({
        type: 'band',
        bandId: 'band-1',
        bandType: 'Band_Duets',
        teamKey: 'p1:p2',
        displayName: 'Player One, Player Two',
      }));
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'stale-player', displayName: 'Stale' }));
      mockFetchOk({ version: '1.0.0' });

      await api.getVersion();

      expect(global.fetch).toHaveBeenCalledWith('/api/version', {
        headers: {
          'X-FST-Selected-Profile-Type': 'band',
          'X-FST-Selected-Profile-Id': 'band-1',
          'X-FST-Selected-Band-Id': 'band-1',
          'X-FST-Selected-Band-Type': 'Band_Duets',
          'X-FST-Selected-Band-Team-Key': 'p1:p2',
        },
      });
    });
  });

  describe('trackPlayer', () => {
    it('posts to track endpoint', async () => {
      mockFetchOk({ accountId: 'p1', displayName: 'Player', trackingStarted: true, backfillStatus: 'queued' });
      const result = await api.trackPlayer('p1');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/track', expect.objectContaining({ method: 'POST' }));
      expect(result.displayName).toBe('Player');
    });

    it('normalizes empty displayName on track response', async () => {
      mockFetchOk({ accountId: 'p1', displayName: '', trackingStarted: true, backfillStatus: 'queued' });
      const result = await api.trackPlayer('p1');
      expect(result.displayName).toBe('Unknown User');
    });
  });

  describe('getSyncStatus', () => {
    it('fetches sync status', async () => {
      mockFetchOk({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null });
      await api.getSyncStatus('p1');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/sync-status', { headers: {} });
    });
  });

  describe('getPlayerHistory', () => {
    it('fetches history with optional songId and instrument', async () => {
      mockFetchOk({ accountId: 'p1', count: 0, history: [] });
      await api.getPlayerHistory('p1', 's1', 'Solo_Guitar');
      expect(global.fetch).toHaveBeenCalledWith(
        expect.stringContaining('songId=s1'),
        { headers: {} },
      );
      expect(global.fetch).toHaveBeenCalledWith(
        expect.stringContaining('instrument=Solo_Guitar'),
        { headers: {} },
      );
    });

    it('fetches history without optional params', async () => {
      mockFetchOk({ accountId: 'p1', count: 0, history: [] });
      await api.getPlayerHistory('p1');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/history', { headers: {} });
    });
  });

  describe('getAllLeaderboards', () => {
    it('fetches all leaderboards for a song', async () => {
      mockFetchOk({ songId: 's1', instruments: [] });
      await api.getAllLeaderboards('s1', 10, 2.0);
      expect(global.fetch).toHaveBeenCalledWith('/api/leaderboard/s1/all?top=10&leeway=2', { headers: {} });
    });
  });

  describe('getPlayerStats', () => {
    it('fetches player stats and preserves band payloads', async () => {
      mockFetchOk({
        accountId: 'p1',
        totalSongs: 10,
        instruments: [],
        bands: {
          all: {
            totalCount: 1,
            entries: [{
              bandId: 'band-guid-1',
              teamKey: 'p1:p2',
              bandType: 'Band_Duets',
              appearanceCount: 2,
              members: [
                { accountId: 'p1', displayName: 'Player One', instruments: ['Solo_Guitar'] },
                { accountId: 'p2', displayName: 'Player Two', instruments: ['Solo_Bass'] },
              ],
            }],
          },
          duos: { totalCount: 1, entries: [] },
          trios: { totalCount: 0, entries: [] },
          quads: { totalCount: 0, entries: [] },
        },
      });
      const result = await api.getPlayerStats('p1');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/stats', { headers: {} });
      expect(result.bands?.all.totalCount).toBe(1);
      expect(result.bands?.all.entries[0]?.bandId).toBe('band-guid-1');
      expect(result.bands?.all.entries[0]?.appearanceCount).toBe(2);
      expect(result.bands?.all.entries[0]?.members[0]?.displayName).toBe('Player One');
    });
  });

  describe('getBandDetail', () => {
    it('fetches band detail by encoded band id', async () => {
      const data = {
        band: { bandId: 'band/guid', teamKey: 'p1:p2', bandType: 'Band_Duets', appearanceCount: 2, members: [] },
        ranking: null,
      };
      mockFetchOk(data);

      const result = await api.getBandDetail('band/guid');

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/bands/band%2Fguid', { headers: {} });
    });
  });

  describe('searchBands', () => {
    it('searches bands with text, explicit accounts, filters, and paging', async () => {
      const data = {
        query: 'SFenton Jasgor9',
        normalizedQuery: 'SFenton Jasgor9',
        rankBy: 'appearance',
        page: 2,
        pageSize: 25,
        totalCount: 0,
        isAmbiguous: false,
        needsDisambiguation: false,
        interpretations: [],
        results: [],
      };
      mockFetchOk(data);

      const result = await api.searchBands({
        q: 'SFenton Jasgor9',
        accountIds: ['acct-1', 'acct-2'],
        bandType: 'Band_Duets',
        combo: 'Solo_Guitar+Solo_Bass',
        rankBy: 'appearance',
        page: 2,
        pageSize: 25,
      });

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/bands/search?q=SFenton+Jasgor9&accountIds=acct-1%2Cacct-2&bandType=Band_Duets&combo=Solo_Guitar%2BSolo_Bass&rankBy=appearance&page=2&pageSize=25', { headers: {} });
    });
  });

  describe('getBandRankHistory', () => {
    it('fetches encoded band rank history with days and combo', async () => {
      const data = { bandType: 'Band_Duets', teamKey: 'p1:p2', comboId: 'Solo_Guitar+Solo_Bass', days: 14, history: [] };
      mockFetchOk(data);

      const result = await api.getBandRankHistory('Band_Duets', 'p1:p2', 14, 'Solo_Guitar+Solo_Bass');

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/rankings/bands/Band_Duets/p1%3Ap2/history?days=14&combo=Solo_Guitar%2BSolo_Bass', { headers: {} });
    });
  });

  describe('getBandSongs', () => {
    it('fetches encoded band songs with limit and combo', async () => {
      const data = { bandType: 'Band_Duets', teamKey: 'p1:p2', comboId: 'Solo_Guitar+Solo_Bass', limit: 5, best: [], worst: [] };
      mockFetchOk(data);

      const result = await api.getBandSongs('Band_Duets', 'p1:p2', 5, 'Solo_Guitar+Solo_Bass');

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/rankings/bands/Band_Duets/p1%3Ap2/songs?limit=5&combo=Solo_Guitar%2BSolo_Bass', { headers: {} });
    });
  });

  describe('getBandSongRows', () => {
    it('fetches encoded band song rows with combo', async () => {
      const data = { bandType: 'Band_Duets', teamKey: 'p1:p2', comboId: 'Solo_Guitar+Solo_Bass', count: 1, entries: [] };
      mockFetchOk(data);

      const result = await api.getBandSongRows('Band_Duets', 'p1:p2', 'Solo_Guitar+Solo_Bass');

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/rankings/bands/Band_Duets/p1%3Ap2/song-rows?combo=Solo_Guitar%2BSolo_Bass', { headers: {} });
    });
  });

  describe('getPlayerBandsList', () => {
    it('fetches paged player bands for a selected group', async () => {
      const data = { accountId: 'p1', group: 'duos', totalCount: 26, entries: [] };
      mockFetchOk(data);

      const result = await api.getPlayerBandsList('p1', 'duos', 2, 25);

      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/bands?group=duos&page=2&pageSize=25', { headers: {} });
    });

    it('passes an abort signal through to the player bands request', async () => {
      const data = { accountId: 'p1', group: 'all', totalCount: 0, entries: [] };
      const controller = new AbortController();
      mockFetchOk(data);

      await api.getPlayerBandsList('p1', 'all', 1, 25, { signal: controller.signal });

      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/bands?group=all&page=1&pageSize=25', {
        headers: {},
        signal: controller.signal,
      });
    });
  });

  describe('getVersion', () => {
    it('fetches version', async () => {
      mockFetchOk({ version: '1.0.0' });
      const result = await api.getVersion();
      expect(result).toEqual({ version: '1.0.0' });
      expect(global.fetch).toHaveBeenCalledWith('/api/version', { headers: {} });
    });

    it('passes a caller signal without dropping selected-profile headers', async () => {
      localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'tracked-1', displayName: 'Tracked' }));
      const controller = new AbortController();
      mockFetchOk({ version: '1.0.0' });

      await api.getVersion({ signal: controller.signal });

      expect(global.fetch).toHaveBeenCalledWith('/api/version', {
        headers: {
          'X-FST-Selected-Profile-Type': 'player',
          'X-FST-Selected-Profile-Id': 'tracked-1',
          'X-FST-Selected-Player': 'tracked-1',
        },
        signal: controller.signal,
      });
    });
  });

  describe('getRivalsOverview', () => {
    it('fetches rivals overview for account', async () => {
      const data = { accountId: 'acc-1', computedAt: '2026-01-01T00:00:00Z', combos: [] };
      mockFetchOk(data);
      const result = await api.getRivalsOverview('acc-1');
      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals', { headers: {} });
    });
  });

  describe('getRivalsList', () => {
    it('fetches rival list for combo', async () => {
      const data = { combo: 'Solo_Guitar', above: [], below: [] };
      mockFetchOk(data);
      const result = await api.getRivalsList('acc-1', 'Solo_Guitar');
      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar', { headers: {} });
    });

    it('encodes combo with special characters', async () => {
      mockFetchOk({ combo: 'Solo_Guitar+Solo_Bass', above: [], below: [] });
      await api.getRivalsList('acc-1', 'Solo_Guitar+Solo_Bass');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar%2BSolo_Bass', { headers: {} });
    });
  });

  describe('getRivalDetail', () => {
    it('fetches rival detail with default sort', async () => {
      const data = { rival: { accountId: 'r1', displayName: 'Rival' }, combo: 'Solo_Guitar', totalSongs: 5, offset: 0, limit: 0, sort: 'closest', songs: [] };
      mockFetchOk(data);
      const result = await api.getRivalDetail('acc-1', 'Solo_Guitar', 'r1');
      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar/r1?limit=0&sort=closest', { headers: {} });
    });

    it('passes custom sort parameter', async () => {
      mockFetchOk({ rival: { accountId: 'r1', displayName: null }, combo: 'Solo_Guitar', totalSongs: 0, offset: 0, limit: 0, sort: 'they_lead', songs: [] });
      await api.getRivalDetail('acc-1', 'Solo_Guitar', 'r1', 'they_lead');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar/r1?limit=0&sort=they_lead', { headers: {} });
    });

    it('passes explicit live fallback options for Find Rival', async () => {
      mockFetchOk({ rival: { accountId: 'r1', displayName: null }, combo: 'Solo_Guitar', totalSongs: 0, offset: 0, limit: 0, sort: 'closest', songs: [] });
      await api.getRivalDetail('acc-1', 'Solo_Guitar', 'r1', 'closest', { allowLiveFallback: true, includeGaps: true });
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar/r1?limit=0&sort=closest&allowLiveFallback=true&includeGaps=true', { headers: {} });
    });

    it('keeps rival response options separate from request cancellation', async () => {
      const controller = new AbortController();
      mockFetchOk({ rival: { accountId: 'r1', displayName: null }, combo: 'Solo_Guitar', totalSongs: 0, offset: 0, limit: 0, sort: 'closest', songs: [] });

      await api.getRivalDetail(
        'acc-1',
        'Solo_Guitar',
        'r1',
        'closest',
        { allowLiveFallback: true },
        { signal: controller.signal },
      );

      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar/r1?limit=0&sort=closest&allowLiveFallback=true', {
        headers: {},
        signal: controller.signal,
      });
    });
  });

  describe('error handling', () => {
    it('throws on non-ok GET response', async () => {
      mockFetchError(404, 'Not Found');
      await expect(api.getLeaderboard('s1', 'Solo_Guitar' as any)).rejects.toThrow('API 404: Not Found');
    });

    it('throws on non-ok POST response', async () => {
      mockFetchError(500, 'Internal Server Error');
      await expect(api.trackPlayer('p1')).rejects.toThrow('API 500: Internal Server Error');
    });
  });

  describe('expandAlbumArt', () => {
    it('prepends CDN prefix to relative album art URLs', () => {
      const songs = [
        { albumArt: 'fortnite/image1.png' },
        { albumArt: 'fortnite/image2.png' },
      ];
      expandAlbumArt(songs);
      expect(songs[0]!.albumArt).toBe('https://cdn2.unrealengine.com/fortnite/image1.png');
      expect(songs[1]!.albumArt).toBe('https://cdn2.unrealengine.com/fortnite/image2.png');
    });

    it('does not modify URLs that already have http prefix', () => {
      const songs = [{ albumArt: 'https://cdn2.unrealengine.com/fortnite/image.png' }];
      expandAlbumArt(songs);
      expect(songs[0]!.albumArt).toBe('https://cdn2.unrealengine.com/fortnite/image.png');
    });

    it('skips songs without albumArt', () => {
      const songs = [{ albumArt: undefined }, { albumArt: 'fortnite/img.png' }];
      expandAlbumArt(songs);
      expect(songs[0]!.albumArt).toBeUndefined();
      expect(songs[1]!.albumArt).toBe('https://cdn2.unrealengine.com/fortnite/img.png');
    });
  });

  describe('getShop', () => {
    it('expands album art URLs in shop response', async () => {
      const shopData = {
        songs: [
          { songId: 's1', title: 'Test', artist: 'A', albumArt: 'fortnite/art.png', shopUrl: 'https://fortnite.com/shop/1' },
          { songId: 's2', title: 'Test2', artist: 'B', shopUrl: 'https://fortnite.com/shop/2' },
        ],
      };
      mockFetchOk(shopData);
      const result = await api.getShop();
      expect(result.songs[0]!.albumArt).toBe('https://cdn2.unrealengine.com/fortnite/art.png');
      expect(result.songs[1]!.albumArt).toBeUndefined();
    });
  });
});
