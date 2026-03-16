import { describe, it, expect } from 'vitest';
import {
  computeInstrumentStats,
  computeOverallStats,
  groupByInstrument,
  formatClamped,
  formatClamped2,
} from '../components/player/playerStats';
import type { PlayerScore } from '../models';

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
    expect(grouped.get('Solo_Guitar' as any)?.length).toBe(2);
    expect(grouped.get('Solo_Bass' as any)?.length).toBe(1);
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
