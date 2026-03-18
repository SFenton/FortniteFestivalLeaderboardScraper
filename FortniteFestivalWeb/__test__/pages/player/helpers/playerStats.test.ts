import { describe, it, expect } from 'vitest';
import {
  computeInstrumentStats,
  computeOverallStats,
  groupByInstrument,
  formatClamped,
  formatClamped2,
} from '../../../../src/pages/player/helpers/playerStats';
import type { PlayerScore } from '@festival/core/api/serverTypes';
import { ACCURACY_SCALE } from '@festival/core';


const makeScore = (overrides: Partial<PlayerScore> = {}): PlayerScore => ({
  songId: 'song-1',
  instrument: 'Solo_Guitar',
  score: 10000,
  rank: 5,
  totalEntries: 100,
  accuracy: 950000,
  isFullCombo: false,
  stars: 5,
  season: 1,
  ...overrides,
});

describe('computeInstrumentStats', () => {
  it('returns zero stats for empty scores', () => {
    const stats = computeInstrumentStats([], 100);
    expect(stats.songsPlayed).toBe(0);
    expect(stats.fcCount).toBe(0);
    expect(stats.avgAccuracy).toBe(0);
  });

  it('computes basic stats correctly', () => {
    const scores = [
      makeScore({ score: 10000, accuracy: 980000, isFullCombo: true, stars: 6 }),
      makeScore({ songId: 'song-2', score: 8000, accuracy: 900000, isFullCombo: false, stars: 4 }),
    ];
    const stats = computeInstrumentStats(scores, 10);
    expect(stats.songsPlayed).toBe(2);
    expect(stats.fcCount).toBe(1);
    expect(stats.goldStarCount).toBe(1);
    expect(stats.fourStarCount).toBe(1);
    expect(stats.completionPercent).toBe('20.0');
  });
});

describe('computeOverallStats', () => {
  it('aggregates across all scores', () => {
    const scores = [
      makeScore({ songId: 'song-1', score: 10000, isFullCombo: true, stars: 6 }),
      makeScore({ songId: 'song-2', score: 5000, isFullCombo: false, stars: 4 }),
    ];
    const stats = computeOverallStats(scores);
    expect(stats.totalScore).toBe(15000);
    expect(stats.songsPlayed).toBe(2);
    expect(stats.fcCount).toBe(1);
    expect(stats.goldStarCount).toBe(1);
  });
});

describe('groupByInstrument', () => {
  it('groups scores by instrument key', () => {
    const scores = [
      makeScore({ instrument: 'Solo_Guitar' }),
      makeScore({ instrument: 'Solo_Bass', songId: 'song-2' }),
      makeScore({ instrument: 'Solo_Guitar', songId: 'song-3' }),
    ];
    const grouped = groupByInstrument(scores);
    expect(grouped.get('Solo_Guitar')?.length).toBe(2);
    expect(grouped.get('Solo_Bass')?.length).toBe(1);
  });
});

describe('formatClamped', () => {
  it('formats whole numbers without decimal', () => {
    expect(formatClamped(5.0)).toBe('5');
  });

  it('formats decimals with one place', () => {
    expect(formatClamped(5.7)).toBe('5.7');
  });

  it('truncates rather than rounding', () => {
    expect(formatClamped(5.99)).toBe('5.9');
  });
});

describe('formatClamped2', () => {
  it('removes trailing zeros', () => {
    expect(formatClamped2(5.00)).toBe('5');
    expect(formatClamped2(5.10)).toBe('5.1');
    expect(formatClamped2(5.12)).toBe('5.12');
  });
});

/* Tests extracted from hooks/AllBranches.test.tsx */
describe('playerStats false-path branches', () => {
  it('empty scores → averageStars=0, avgAcc=0, bestAcc=0, avgScore=0', () => {
    const s = computeInstrumentStats([], 10);
    expect(s.averageStars).toBe(0);
    expect(s.avgAccuracy).toBe(0);
    expect(s.bestAccuracy).toBe(0);
    expect(s.avgScore).toBe(0);
    expect(s.bestRank).toBe(0);
    expect(s.bestRankSongId).toBeNull();
    expect(s.percentileBuckets.length).toBe(0);
  });

  it('scores with stars=undefined → triggers ?? 0 fallback in all star filters', () => {
    const s = computeInstrumentStats([makeScore({ stars: undefined, accuracy: undefined, rank: undefined as any, totalEntries: undefined })], 10);
    expect(s.goldStarCount).toBe(0);
    expect(s.fiveStarCount).toBe(0);
    expect(s.fourStarCount).toBe(0);
    expect(s.threeStarCount).toBe(0);
    expect(s.twoStarCount).toBe(0);
    expect(s.oneStarCount).toBe(0);
    expect(s.averageStars).toBe(0);
    expect(s.avgAccuracy).toBe(0);
    expect(s.bestRank).toBe(0);
    expect(s.bestRankSongId).toBeNull();
  });

  it('scores with accuracy=0 → accuracies empty → avgAcc=0', () => {
    const s = computeInstrumentStats([makeScore({ accuracy: 0 })], 10);
    expect(s.avgAccuracy).toBe(0);
  });

  it('scores with rank=0 → no ranked → bestRank=0, bestRankSongId=null', () => {
    const s = computeInstrumentStats([makeScore({ rank: 0, totalEntries: 0 })], 10);
    expect(s.bestRank).toBe(0);
    expect(s.bestRankSongId).toBeNull();
  });

  it('scores with no percentile data → percentileBuckets empty', () => {
    const s = computeInstrumentStats([makeScore({ rank: 0, totalEntries: 0 })], 10);
    expect(s.percentileBuckets.length).toBe(0);
  });

  it('computeOverallStats with undefined fields → triggers ?? fallbacks', () => {
    const s = computeOverallStats([makeScore({ rank: undefined as any, totalEntries: undefined, stars: undefined, accuracy: undefined, isFullCombo: undefined as any })]);
    expect(s.bestRank).toBe(0);
    expect(s.bestRankSongId).toBeNull();
  });

  it('computeOverallStats with empty scores', () => {
    const s = computeOverallStats([]);
    expect(s.songsPlayed).toBe(0);
    expect(s.avgAccuracy).toBe(0);
  });
});

/* Tests extracted from hooks/ComprehensiveBranches.test.tsx */
describe('playerStats — complete branch coverage', () => {
  describe('computeInstrumentStats', () => {
    it('handles scores with stars=0 (averageStars=0 branch)', () => {
      const stats = computeInstrumentStats([makeScore({ stars: 0 })], 10);
      expect(stats.averageStars).toBe(0);
    });

    it('computes averageStars when stars > 0', () => {
      const stats = computeInstrumentStats([makeScore({ stars: 5 }), makeScore({ songId: 's2', stars: 3 })], 10);
      expect(stats.averageStars).toBe(4);
    });

    it('handles no ranked scores (bestRank=0)', () => {
      const stats = computeInstrumentStats([makeScore({ rank: 0, totalEntries: 0 })], 10);
      expect(stats.bestRank).toBe(0);
      expect(stats.bestRankSongId).toBeNull();
    });

    it('finds bestRankSongId from ranked scores', () => {
      const stats = computeInstrumentStats([
        makeScore({ songId: 's1', rank: 5, totalEntries: 100 }),
        makeScore({ songId: 's2', rank: 1, totalEntries: 100 }),
      ], 10);
      expect(stats.bestRank).toBe(1);
      expect(stats.bestRankSongId).toBe('s2');
    });

    it('counts all star levels', () => {
      const scores = [
        makeScore({ songId: 'a', stars: 6 }),
        makeScore({ songId: 'b', stars: 5 }),
        makeScore({ songId: 'c', stars: 4 }),
        makeScore({ songId: 'd', stars: 3 }),
        makeScore({ songId: 'e', stars: 2 }),
        makeScore({ songId: 'f', stars: 1 }),
      ];
      const stats = computeInstrumentStats(scores, 10);
      expect(stats.goldStarCount).toBe(1);
      expect(stats.fiveStarCount).toBe(1);
      expect(stats.fourStarCount).toBe(1);
      expect(stats.threeStarCount).toBe(1);
      expect(stats.twoStarCount).toBe(1);
      expect(stats.oneStarCount).toBe(1);
    });

    it('computes FC percentage correctly', () => {
      const scores = [makeScore({ isFullCombo: true }), makeScore({ songId: 's2', isFullCombo: false })];
      const stats = computeInstrumentStats(scores, 10);
      expect(stats.fcCount).toBe(1);
      expect(parseFloat(stats.fcPercent)).toBe(50);
    });

    it('100% FC case', () => {
      const scores = [makeScore({ isFullCombo: true })];
      const stats = computeInstrumentStats(scores, 1);
      expect(stats.fcPercent).toBe('100.0');
    });

    it('handles empty accuracies gracefully', () => {
      const stats = computeInstrumentStats([makeScore({ accuracy: 0 })], 10);
      expect(stats.avgAccuracy).toBe(0);
    });
  });

  describe('computeOverallStats', () => {
    it('counts unique songs played', () => {
      const stats = computeOverallStats([makeScore({ songId: 's1' }), makeScore({ songId: 's1' }), makeScore({ songId: 's2' })]);
      expect(stats.songsPlayed).toBe(2);
    });

    it('computes best rank across all scores', () => {
      const stats = computeOverallStats([makeScore({ rank: 10 }), makeScore({ songId: 's2', rank: 3 })]);
      expect(stats.bestRank).toBe(3);
    });

    it('handles no ranked scores', () => {
      const stats = computeOverallStats([makeScore({ rank: 0 })]);
      expect(stats.bestRank).toBe(0);
      expect(stats.bestRankSongId).toBeNull();
    });

    it('computes FC percentage', () => {
      const stats = computeOverallStats([makeScore({ isFullCombo: true }), makeScore({ songId: 's2', isFullCombo: false })]);
      expect(stats.fcCount).toBe(1);
    });

    it('computes avgAccuracy', () => {
      const stats = computeOverallStats([makeScore({ accuracy: 95 * ACCURACY_SCALE }), makeScore({ songId: 's2', accuracy: 85 * ACCURACY_SCALE })]);
      expect(stats.avgAccuracy).toBe(90 * ACCURACY_SCALE);
    });

    it('finds bestRankSongId and bestRankInstrument', () => {
      const stats = computeOverallStats([
        makeScore({ songId: 's1', rank: 5, instrument: 'Solo_Guitar' }),
        makeScore({ songId: 's2', rank: 1, instrument: 'Solo_Bass' }),
      ]);
      expect(stats.bestRankSongId).toBe('s2');
      expect(stats.bestRankInstrument).toBe('Solo_Bass');
    });
  });
});
