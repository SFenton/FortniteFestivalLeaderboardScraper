import { describe, it, expect, beforeEach } from 'vitest';
import {
  songDetailCache,
  clearSongDetailCache,
  leaderboardCache,
  clearLeaderboardCache,
  clearPlayerPageCache,
  type SongDetailCache,
  type LeaderboardCache,
} from '../../src/api/pageCache';

describe('pageCache', () => {
  beforeEach(() => {
    clearSongDetailCache();
    clearLeaderboardCache();
  });

  describe('songDetailCache', () => {
    it('stores and retrieves entries', () => {
      const entry: SongDetailCache = {
        scrollTop: 42,
      };
      songDetailCache.set('song-1', entry);
      expect(songDetailCache.get('song-1')).toBe(entry);
      expect(songDetailCache.get('song-1')).toEqual({ scrollTop: 42 });
    });

    it('clearSongDetailCache removes all entries', () => {
      songDetailCache.set('song-1', {} as SongDetailCache);
      songDetailCache.set('song-2', {} as SongDetailCache);
      clearSongDetailCache();
      expect(songDetailCache.size).toBe(0);
    });
  });

  describe('leaderboardCache', () => {
    it('stores and retrieves entries', () => {
      const entry: LeaderboardCache = {
        page: 2,
        scrollTop: 500,
      };
      leaderboardCache.set('key-1', entry);
      expect(leaderboardCache.get('key-1')).toBe(entry);
      expect(leaderboardCache.get('key-1')).toEqual({ page: 2, scrollTop: 500 });
    });

    it('clearLeaderboardCache removes all entries', () => {
      leaderboardCache.set('key-1', {} as LeaderboardCache);
      clearLeaderboardCache();
      expect(leaderboardCache.size).toBe(0);
    });
  });

  describe('clearPlayerPageCache', () => {
    it('can be called without error (no-op)', () => {
      expect(() => clearPlayerPageCache()).not.toThrow();
    });
  });
});
