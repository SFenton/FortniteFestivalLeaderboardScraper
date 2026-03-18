import { describe, it, expect, beforeEach } from 'vitest';
import {
  songDetailCache,
  clearSongDetailCache,
  leaderboardCache,
  clearLeaderboardCache,
  clearPlayerPageCache,
  type SongDetailCache,
  type LeaderboardCache,
} from '../../api/pageCache';

describe('pageCache', () => {
  beforeEach(() => {
    clearSongDetailCache();
    clearLeaderboardCache();
  });

  describe('songDetailCache', () => {
    it('stores and retrieves entries', () => {
      const entry: SongDetailCache = {
        instrumentData: {} as SongDetailCache['instrumentData'],
        playerScores: [],
        scoreHistory: [],
        accountId: 'acc-1',
        scrollTop: 42,
      };
      songDetailCache.set('song-1', entry);
      expect(songDetailCache.get('song-1')).toBe(entry);
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
        entries: [],
        totalEntries: 100,
        localEntries: 100,
        page: 2,
        scrollTop: 500,
      };
      leaderboardCache.set('key-1', entry);
      expect(leaderboardCache.get('key-1')).toBe(entry);
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
