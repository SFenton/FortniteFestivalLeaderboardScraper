import { describe, it, expect, beforeEach } from 'vitest';
import { loadLeaderboardRankBy, saveLeaderboardRankBy } from '../../src/utils/leaderboardSettings';

describe('leaderboardSettings', () => {
  beforeEach(() => { localStorage.clear(); });

  describe('loadLeaderboardRankBy', () => {
    it('returns totalscore when no stored value', () => {
      expect(loadLeaderboardRankBy()).toBe('totalscore');
    });

    it('returns saved metric after save', () => {
      saveLeaderboardRankBy('adjusted');
      expect(loadLeaderboardRankBy()).toBe('adjusted');
    });

    it('returns totalscore for corrupted localStorage', () => {
      localStorage.setItem('fst:leaderboardSettings', 'not-json');
      expect(loadLeaderboardRankBy()).toBe('totalscore');
    });

    it('returns totalscore for invalid metric string', () => {
      localStorage.setItem('fst:leaderboardSettings', JSON.stringify({ rankBy: 'nonexistent' }));
      expect(loadLeaderboardRankBy()).toBe('totalscore');
    });

    it('returns totalscore when rankBy key is missing', () => {
      localStorage.setItem('fst:leaderboardSettings', JSON.stringify({}));
      expect(loadLeaderboardRankBy()).toBe('totalscore');
    });

    it('returns totalscore when rankBy is not a string', () => {
      localStorage.setItem('fst:leaderboardSettings', JSON.stringify({ rankBy: 42 }));
      expect(loadLeaderboardRankBy()).toBe('totalscore');
    });

    it('roundtrips all valid metrics', () => {
      for (const metric of ['totalscore', 'adjusted', 'weighted', 'fcrate', 'maxscore'] as const) {
        saveLeaderboardRankBy(metric);
        expect(loadLeaderboardRankBy()).toBe(metric);
      }
    });
  });

  describe('saveLeaderboardRankBy', () => {
    it('writes to localStorage', () => {
      saveLeaderboardRankBy('weighted');
      const raw = localStorage.getItem('fst:leaderboardSettings');
      expect(raw).toBeTruthy();
      expect(JSON.parse(raw!)).toEqual({ rankBy: 'weighted' });
    });

    it('overwrites previous value', () => {
      saveLeaderboardRankBy('adjusted');
      saveLeaderboardRankBy('fcrate');
      expect(loadLeaderboardRankBy()).toBe('fcrate');
    });
  });
});
