import { describe, it, expect } from 'vitest';
import { computeRankWidth, formatRating, getRatingForMetric, getSongsLabel } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
import type { AccountRankingEntry } from '@festival/core/api/serverTypes';
import type { RankingMetric } from '@festival/core/api/serverTypes';
import { Layout } from '@festival/theme';

describe('computeRankWidth', () => {
  it('returns default rankColumnWidth for empty array', () => {
    expect(computeRankWidth([])).toBe(Layout.rankColumnWidth);
  });

  it('computes width based on the longest formatted rank', () => {
    const width = computeRankWidth([1, 50, 12345]);
    // Longest is "#12,345" = 7 chars → Math.ceil(7 * 8.5) + 12 = 72
    expect(width).toBe(Math.ceil('#12,345'.length * Layout.rankCharWidth) + Layout.rankColumnPadding);
  });

  it('handles a single rank', () => {
    const width = computeRankWidth([5]);
    // "#5" = 2 chars → Math.ceil(2 * 8.5) + 12 = 29
    expect(width).toBe(Math.ceil('#5'.length * Layout.rankCharWidth) + Layout.rankColumnPadding);
  });

  it('handles large ranks with locale separators', () => {
    const width = computeRankWidth([1234567]);
    const formatted = `#${(1234567).toLocaleString()}`;
    expect(width).toBe(Math.ceil(formatted.length * Layout.rankCharWidth) + Layout.rankColumnPadding);
  });

  it('picks the max rank when multiple are given', () => {
    const small = computeRankWidth([1]);
    const large = computeRankWidth([1, 999999]);
    expect(large).toBeGreaterThan(small);
  });
});

describe('formatRating', () => {
  it('returns empty string for adjusted metric', () => {
    expect(formatRating(0.038, 'adjusted')).toBe('');
  });

  it('returns empty string for weighted metric', () => {
    expect(formatRating(0.042, 'weighted')).toBe('');
  });

  it('formats fcrate as percentage', () => {
    expect(formatRating(0.653, 'fcrate')).toBe('65.3%');
  });

  it('formats maxscore as percentage', () => {
    expect(formatRating(0.941, 'maxscore')).toBe('94.1%');
  });

  it('formats totalscore with locale separators', () => {
    expect(formatRating(1250000, 'totalscore')).toBe((1250000).toLocaleString());
  });
});

describe('getRatingForMetric – fcrate uses raw ratio', () => {
  const entry = {
    fullComboCount: 631,
    songsPlayed: 632,
    totalChartedSongs: 633,
    fcRate: 0.962,
  } as AccountRankingEntry;

  it('returns raw fullComboCount/totalChartedSongs, not Bayesian fcRate', () => {
    expect(getRatingForMetric(entry, 'fcrate')).toBeCloseTo(631 / 633);
  });

  it('returns 0 when totalChartedSongs is 0', () => {
    const empty = { fullComboCount: 0, songsPlayed: 0, totalChartedSongs: 0, fcRate: 0 } as AccountRankingEntry;
    expect(getRatingForMetric(empty, 'fcrate')).toBe(0);
  });
});

describe('getSongsLabel', () => {
  const entry = { fullComboCount: 631, songsPlayed: 632, totalChartedSongs: 633 };

  it('returns fullComboCount / totalChartedSongs for fcrate', () => {
    expect(getSongsLabel(entry, 'fcrate')).toBe('631 / 633');
  });

  it.each<RankingMetric>(['adjusted', 'weighted', 'totalscore', 'maxscore'])(
    'returns songsPlayed / totalChartedSongs for %s',
    (metric) => {
      expect(getSongsLabel(entry, metric)).toBe('632 / 633');
    },
  );
});
