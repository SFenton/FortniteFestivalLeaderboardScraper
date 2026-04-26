import { describe, it, expect } from 'vitest';
import { LEADERBOARD_PAGE_SIZE, computePillMinWidth, computeRankWidth, formatBayesianRatingDisplay, formatRating, formatRankingValueDisplay, getBayesianRatingForMetric, getLeaderboardPageForRank, getRatingForMetric, getRatingPillTier, getSongsLabel } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
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

describe('getLeaderboardPageForRank', () => {
  it('returns page 1 for the first rank', () => {
    expect(getLeaderboardPageForRank(1)).toBe(1);
  });

  it('keeps the last rank on a page on that same page', () => {
    expect(getLeaderboardPageForRank(LEADERBOARD_PAGE_SIZE)).toBe(1);
  });

  it('moves the first rank after a page boundary to the next page', () => {
    expect(getLeaderboardPageForRank(LEADERBOARD_PAGE_SIZE + 1)).toBe(2);
  });

  it('maps later boundaries to the expected page', () => {
    expect(getLeaderboardPageForRank(51)).toBe(3);
  });

  it('falls back to page 1 for invalid ranks', () => {
    expect(getLeaderboardPageForRank(0)).toBe(1);
    expect(getLeaderboardPageForRank(Number.NaN)).toBe(1);
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

describe('formatRankingValueDisplay', () => {
  it('formats adjusted and weighted raw percentile ratings as Top percent labels', () => {
    expect(formatRankingValueDisplay(0.0056, 'adjusted')).toBe('Top 0.56%');
    expect(formatRankingValueDisplay(0.042, 'weighted')).toBe('Top 4%');
  });

  it('returns undefined for non-percentile ranking metrics', () => {
    expect(formatRankingValueDisplay(0.98, 'fcrate')).toBeUndefined();
    expect(formatRankingValueDisplay(0.98, 'maxscore')).toBeUndefined();
    expect(formatRankingValueDisplay(123456, 'totalscore')).toBeUndefined();
  });
});

describe('formatBayesianRatingDisplay', () => {
  it('formats adjusted and weighted Bayesian values as raw value labels', () => {
    expect(formatBayesianRatingDisplay(0.0409, 'adjusted')).toBe('0.0409');
    expect(formatBayesianRatingDisplay(0.9123, 'weighted')).toBe('0.91');
  });

  it('returns undefined for non-percentile ranking metrics', () => {
    expect(formatBayesianRatingDisplay(0.98, 'fcrate')).toBeUndefined();
    expect(formatBayesianRatingDisplay(undefined, 'adjusted')).toBeUndefined();
  });
});

describe('computePillMinWidth', () => {
  it('uses the widest label for a shared pill width', () => {
    const width = computePillMinWidth(['Top 2%', 'Top 0.56%']);
    expect(width).toBe(Math.ceil('Top 0.56%'.length * Layout.rankCharWidth) + Layout.rankColumnPadding);
  });

  it('returns undefined when no labels are present', () => {
    expect(computePillMinWidth([undefined, null])).toBeUndefined();
  });
});

describe('getRatingPillTier', () => {
  it.each<RankingMetric>(['fcrate', 'maxscore'])('tiers %s percentage metrics', (metric) => {
    expect(getRatingPillTier(0.99, metric)).toBe('top1');
    expect(getRatingPillTier(0.95, metric)).toBe('top5');
    expect(getRatingPillTier(0.94, metric)).toBe('default');
  });

  it.each<RankingMetric>(['adjusted', 'weighted', 'totalscore'])('does not tier %s', (metric) => {
    expect(getRatingPillTier(0.99, metric)).toBeUndefined();
  });
});

describe('getRatingForMetric – adjusted/weighted use raw values', () => {
  const entry = {
    rawSkillRating: 0.005,
    adjustedSkillRating: 0.042,
    weightedRating: 0.041,
    rawWeightedRating: 0.004,
    fullComboCount: 631,
    songsPlayed: 632,
    totalChartedSongs: 633,
    fcRate: 0.962,
  } as AccountRankingEntry;

  it('returns rawSkillRating for adjusted metric', () => {
    expect(getRatingForMetric(entry, 'adjusted')).toBe(0.005);
  });

  it('returns adjustedSkillRating for adjusted Bayesian display', () => {
    expect(getBayesianRatingForMetric(entry, 'adjusted')).toBe(0.042);
  });

  it('returns rawWeightedRating for weighted metric', () => {
    expect(getRatingForMetric(entry, 'weighted')).toBe(0.004);
  });

  it('returns weightedRating for weighted Bayesian display', () => {
    expect(getBayesianRatingForMetric(entry, 'weighted')).toBe(0.041);
  });

  it('falls back to weightedRating when rawWeightedRating is null', () => {
    const noRaw = { ...entry, rawWeightedRating: null } as unknown as AccountRankingEntry;
    expect(getRatingForMetric(noRaw, 'weighted')).toBe(0.041);
  });

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
