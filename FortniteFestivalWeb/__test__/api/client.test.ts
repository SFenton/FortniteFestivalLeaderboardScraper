import { describe, it, expect, vi, beforeEach } from 'vitest';
import { api, expandAlbumArt } from '../../src/api/client';

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
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

describe('api/client', () => {
  describe('getSongs', () => {
    it('fetches songs from /api/songs', async () => {
      const data = { songs: [{ songId: 's1', title: 'Test' }], count: 1, currentSeason: 5 };
      mockFetchOk(data);
      const result = await api.getSongs();
      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/songs', { headers: {} });
    });

    it('sends If-None-Match when cached ETag exists', async () => {
      // Seed localStorage with a cached response + etag
      const cached = { songs: [{ songId: 's1', title: 'Old' }], count: 1, currentSeason: 5 };
      localStorage.setItem('fst_songs_cache', JSON.stringify({ data: cached, etag: '"abc123"' }));

      (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
        ok: false,
        status: 304,
        headers: new Headers(),
      });

      const result = await api.getSongs();
      expect(global.fetch).toHaveBeenCalledWith('/api/songs', { headers: { 'If-None-Match': '"abc123"' } });
      expect(result).toEqual(cached);
    });

    it('updates cache on 200 with new ETag', async () => {
      const data = { songs: [{ songId: 's2', title: 'New' }], count: 1, currentSeason: 6 };
      (global.fetch as ReturnType<typeof vi.fn>).mockResolvedValue({
        ok: true,
        status: 200,
        json: () => Promise.resolve(data),
        headers: new Headers({ etag: '"newetag"' }),
      });

      await api.getSongs();
      const stored = JSON.parse(localStorage.getItem('fst_songs_cache')!);
      expect(stored.etag).toBe('"newetag"');
      expect(stored.data.songs[0].songId).toBe('s2');
    });
  });

  describe('getLeaderboard', () => {
    it('fetches leaderboard with correct URL params', async () => {
      const data = { songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] };
      mockFetchOk(data);
      await api.getLeaderboard('s1', 'Solo_Guitar' as any, 50, 10);
      expect(global.fetch).toHaveBeenCalledWith('/api/leaderboard/s1/Solo_Guitar?top=50&offset=10');
    });

    it('includes leeway param when provided', async () => {
      mockFetchOk({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
      await api.getLeaderboard('s1', 'Solo_Guitar' as any, 100, 0, 1.5);
      expect(global.fetch).toHaveBeenCalledWith('/api/leaderboard/s1/Solo_Guitar?top=100&offset=0&leeway=1.5');
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
      expect(global.fetch).toHaveBeenCalledWith('/api/account/search?q=test&limit=5');
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
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/sync-status');
    });
  });

  describe('getPlayerHistory', () => {
    it('fetches history with optional songId and instrument', async () => {
      mockFetchOk({ accountId: 'p1', count: 0, history: [] });
      await api.getPlayerHistory('p1', 's1', 'Solo_Guitar');
      expect(global.fetch).toHaveBeenCalledWith(expect.stringContaining('songId=s1'));
      expect(global.fetch).toHaveBeenCalledWith(expect.stringContaining('instrument=Solo_Guitar'));
    });

    it('fetches history without optional params', async () => {
      mockFetchOk({ accountId: 'p1', count: 0, history: [] });
      await api.getPlayerHistory('p1');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/history');
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
    it('fetches player stats', async () => {
      mockFetchOk({ accountId: 'p1', stats: [] });
      await api.getPlayerStats('p1');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/p1/stats');
    });
  });

  describe('getVersion', () => {
    it('fetches version', async () => {
      mockFetchOk({ version: '1.0.0' });
      const result = await api.getVersion();
      expect(result).toEqual({ version: '1.0.0' });
    });
  });

  describe('getRivalsOverview', () => {
    it('fetches rivals overview for account', async () => {
      const data = { accountId: 'acc-1', computedAt: '2026-01-01T00:00:00Z', combos: [] };
      mockFetchOk(data);
      const result = await api.getRivalsOverview('acc-1');
      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals');
    });
  });

  describe('getRivalsList', () => {
    it('fetches rival list for combo', async () => {
      const data = { combo: 'Solo_Guitar', above: [], below: [] };
      mockFetchOk(data);
      const result = await api.getRivalsList('acc-1', 'Solo_Guitar');
      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar');
    });

    it('encodes combo with special characters', async () => {
      mockFetchOk({ combo: 'Solo_Guitar+Solo_Bass', above: [], below: [] });
      await api.getRivalsList('acc-1', 'Solo_Guitar+Solo_Bass');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar%2BSolo_Bass');
    });
  });

  describe('getRivalDetail', () => {
    it('fetches rival detail with default sort', async () => {
      const data = { rival: { accountId: 'r1', displayName: 'Rival' }, combo: 'Solo_Guitar', totalSongs: 5, offset: 0, limit: 0, sort: 'closest', songs: [] };
      mockFetchOk(data);
      const result = await api.getRivalDetail('acc-1', 'Solo_Guitar', 'r1');
      expect(result).toEqual(data);
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar/r1?limit=0&sort=closest');
    });

    it('passes custom sort parameter', async () => {
      mockFetchOk({ rival: { accountId: 'r1', displayName: null }, combo: 'Solo_Guitar', totalSongs: 0, offset: 0, limit: 0, sort: 'they_lead', songs: [] });
      await api.getRivalDetail('acc-1', 'Solo_Guitar', 'r1', 'they_lead');
      expect(global.fetch).toHaveBeenCalledWith('/api/player/acc-1/rivals/Solo_Guitar/r1?limit=0&sort=they_lead');
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
      expect(songs[0].albumArt).toBe('https://cdn2.unrealengine.com/fortnite/image1.png');
      expect(songs[1].albumArt).toBe('https://cdn2.unrealengine.com/fortnite/image2.png');
    });

    it('does not modify URLs that already have http prefix', () => {
      const songs = [{ albumArt: 'https://cdn2.unrealengine.com/fortnite/image.png' }];
      expandAlbumArt(songs);
      expect(songs[0].albumArt).toBe('https://cdn2.unrealengine.com/fortnite/image.png');
    });

    it('skips songs without albumArt', () => {
      const songs = [{ albumArt: undefined }, { albumArt: 'fortnite/img.png' }];
      expandAlbumArt(songs);
      expect(songs[0].albumArt).toBeUndefined();
      expect(songs[1].albumArt).toBe('https://cdn2.unrealengine.com/fortnite/img.png');
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
      expect(result.songs[0].albumArt).toBe('https://cdn2.unrealengine.com/fortnite/art.png');
      expect(result.songs[1].albumArt).toBeUndefined();
    });
  });
});
