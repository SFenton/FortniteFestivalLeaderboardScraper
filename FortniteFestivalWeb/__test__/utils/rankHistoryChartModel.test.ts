import { describe, expect, it } from 'vitest';
import type { RankHistoryEntry } from '@festival/core/api';
import {
  formatDetailValue,
  formatRankHistoryAxisDate,
  formatRankHistoryDisplayDate,
  formatRankHistoryFcFraction,
  formatRankHistorySongsFraction,
  formatValueTick,
  getRankHistoryDomain,
  getRankHistoryTotalSongCount,
  getRecentRankHistoryPoints,
  isSameRankHistoryPoint,
  toRankHistoryChartPoint,
  type RankHistoryChartPoint,
} from '../../src/utils/rankHistoryChartModel';

const entry: RankHistoryEntry = {
  snapshotDate: '2026-04-22',
  snapshotTakenAt: '2026-04-22T06:30:00.000Z',
  adjustedSkillRank: 7,
  weightedRank: 8,
  fcRateRank: 9,
  totalScoreRank: 10,
  maxScorePercentRank: 11,
  adjustedSkillRating: 0.61,
  weightedRating: 0.51,
  fcRate: 0.5,
  totalScore: 1_234_567,
  maxScorePercent: 0.9,
  songsPlayed: 10,
  coverage: 0.5,
  fullComboCount: 4,
  totalChartedSongs: 20,
  rankedAccountCount: 200,
  rawSkillRating: 0.62,
  rawWeightedRating: 0.52,
  rawMaxScorePercent: 0.91,
};

describe('rankHistoryChartModel', () => {
  it('maps API entries with raw and Bayesian values intact', () => {
    const point = toRankHistoryChartPoint(entry, 'adjusted');

    expect(point).toMatchObject({
      date: '2026-04-22',
      dateLabel: '4/22/26',
      rank: 7,
      value: 0.62,
      bayesianValue: 0.61,
      totalChartedSongs: 20,
      rankedAccountCount: 200,
    });
  });

  it('formats snapshot dates from the intended calendar day', () => {
    expect(formatRankHistoryAxisDate(new Date(2026, 3, 22))).toBe('4/22/26');
    expect(formatRankHistoryDisplayDate('2026-04-22')).toBe('Apr 22, 2026');
  });

  it('formats metric values without changing existing precision', () => {
    expect(formatValueTick(1_250_000, 'totalscore')).toBe('1.3M');
    expect(formatValueTick(0.995, 'fcrate')).toBe('100%');
    expect(formatDetailValue(0.995, 'fcrate')).toBe('99.5%');
    expect(formatDetailValue(1_250_000, 'totalscore')).toBe('1,250,000');
  });

  it('derives total-song and FC fractions from explicit or coverage data', () => {
    const point = toRankHistoryChartPoint(entry, 'fcrate');
    expect(getRankHistoryTotalSongCount(point)).toBe(20);
    expect(formatRankHistoryFcFraction(point)).toBe('4 / 20');
    expect(formatRankHistorySongsFraction(point)).toBe('10 / 20');

    const coveragePoint = {
      ...point,
      totalChartedSongs: null,
      songsPlayed: 9,
      coverage: 0.75,
    };
    expect(getRankHistoryTotalSongCount(coveragePoint)).toBe(12);
  });

  it('owns point identity, recent selection, and padded rank domains', () => {
    const points = [
      point('2026-04-20', 10, 1),
      point('2026-04-21', 20, 2),
      point('2026-04-22', 30, 3),
    ];

    expect(isSameRankHistoryPoint(points[0]!, { ...points[0]! })).toBe(true);
    expect(isSameRankHistoryPoint(points[0]!, points[1]!)).toBe(false);
    expect(getRecentRankHistoryPoints(points, 2)).toEqual([
      points[2],
      points[1],
    ]);
    expect(getRankHistoryDomain(points)).toEqual([8, 32]);
    expect(getRankHistoryDomain([])).toEqual([1, 100]);
  });
});

function point(
  date: string,
  rank: number,
  value: number,
): RankHistoryChartPoint {
  return {
    date,
    dateLabel: date,
    timestamp: 0,
    snapshotTakenAt: null,
    isSynthetic: false,
    value,
    rank,
    songsPlayed: null,
    coverage: null,
    fullComboCount: null,
    totalChartedSongs: null,
    rankedAccountCount: null,
  };
}
