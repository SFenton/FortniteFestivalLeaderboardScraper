import {
  formatPercentileBucket,
  formatLeaderboardPercentile,
  formatPercentileTopExact,
  formatScoreCompact,
  instrumentKeyToColorHex,
  instrumentKeyToLabel,
  accuracyColor,
  accuracyBgColor,
  calculateScoreWidth,
  formatRatingValue,
  maxScoreColor,
  rankColor,
} from '../formatters';

describe('app/format/formatters', () => {
  describe('formatScoreCompact', () => {
    test('formats < 1k as whole number with commas', () => {
      expect(formatScoreCompact(0)).toBe('0');
      expect(formatScoreCompact(12)).toBe('12');
      expect(formatScoreCompact(999)).toBe('999');
      expect(formatScoreCompact(123456)).toBe('123.5K');
    });

    test('formats thousands as K with one decimal', () => {
      expect(formatScoreCompact(1000)).toBe('1.0K');
      expect(formatScoreCompact(1500)).toBe('1.5K');
    });

    test('formats millions and billions', () => {
      expect(formatScoreCompact(1_000_000)).toBe('1.00M');
      expect(formatScoreCompact(2_500_000)).toBe('2.50M');
      expect(formatScoreCompact(1_234_000_000)).toBe('1.23B');
    });

    test('handles negatives and non-finite', () => {
      expect(formatScoreCompact(-1500)).toBe('-1.5K');
      expect(formatScoreCompact(Number.NaN)).toBe('N/A');
      expect(formatScoreCompact(Number.POSITIVE_INFINITY)).toBe('N/A');
    });
  });

  describe('formatPercentileTopExact', () => {
    test('clamps to 0.01% minimum and shows 2 decimals under 1%', () => {
      expect(formatPercentileTopExact(0)).toBe('Top 0.01%');
      expect(formatPercentileTopExact(0.0001)).toBe('Top 0.01%');
      expect(formatPercentileTopExact(0.0099)).toBe('Top 0.99%');
    });

    test('shows whole percent at 1%+', () => {
      expect(formatPercentileTopExact(0.01)).toBe('Top 1%');
      expect(formatPercentileTopExact(0.0144)).toBe('Top 1%');
      expect(formatPercentileTopExact(0.5)).toBe('Top 50%');
      expect(formatPercentileTopExact(2)).toBe('Top 100%');
    });

    test('handles non-finite', () => {
      expect(formatPercentileTopExact(Number.NaN)).toBe('N/A');
      expect(formatPercentileTopExact(Number.POSITIVE_INFINITY)).toBe('N/A');
    });
  });

  describe('formatPercentileBucket', () => {
    test('clamps values below 1 to Top 1%', () => {
      expect(formatPercentileBucket(0.5)).toBe('Top 1%');
      expect(formatPercentileBucket(0.01)).toBe('Top 1%');
    });

    test('snaps to exact bucket boundaries', () => {
      expect(formatPercentileBucket(1)).toBe('Top 1%');
      expect(formatPercentileBucket(2)).toBe('Top 2%');
      expect(formatPercentileBucket(5)).toBe('Top 5%');
      expect(formatPercentileBucket(10)).toBe('Top 10%');
    });

    test('rounds up to next bucket', () => {
      expect(formatPercentileBucket(1.5)).toBe('Top 2%');
      expect(formatPercentileBucket(5.01)).toBe('Top 10%');
      expect(formatPercentileBucket(15.5)).toBe('Top 20%');
    });

    test('clamps values above 100', () => {
      expect(formatPercentileBucket(100)).toBe('Top 100%');
      expect(formatPercentileBucket(150)).toBe('Top 100%');
    });
  });

  describe('formatLeaderboardPercentile', () => {
    test('returns undefined without a population', () => {
      expect(formatLeaderboardPercentile(1, 0)).toBeUndefined();
    });

    test('uses granular and clamped percentile buckets', () => {
      expect(formatLeaderboardPercentile(1, 10_000)).toBe('Top 0.01%');
      expect(formatLeaderboardPercentile(5, 100)).toBe('Top 5%');
      expect(formatLeaderboardPercentile(500, 100)).toBe('Top 100%');
    });
  });

  describe('instrument presentation', () => {
    test('maps keys to labels', () => {
      expect(instrumentKeyToLabel('guitar')).toBe('Lead');
      expect(instrumentKeyToLabel('pro_guitar')).toBe('Pro Lead');
    });

    test('maps keys to colors', () => {
      expect(instrumentKeyToColorHex('guitar')).toBe('#b35cd6');
      expect(instrumentKeyToColorHex('vocals')).toBe('#27ae60');
    });
  });

  describe('accuracyColor', () => {
    test('returns red at 0%', () => {
      expect(accuracyColor(0)).toBe('rgb(220,40,40)');
    });

    test('returns green at 100%', () => {
      expect(accuracyColor(100)).toBe('rgb(46,204,113)');
    });

    test('returns interpolated color at 50%', () => {
      expect(accuracyColor(50)).toBe('rgb(133,122,77)');
    });

    test('clamps below 0', () => {
      expect(accuracyColor(-10)).toBe('rgb(220,40,40)');
    });

    test('clamps above 100', () => {
      expect(accuracyColor(150)).toBe('rgb(46,204,113)');
    });
  });

  describe('related color helpers', () => {
    test('formats accuracy backgrounds and rank colors', () => {
      expect(accuracyBgColor(0)).toBe('rgba(220,40,40,0.25)');
      expect(accuracyBgColor(100)).toBe('rgba(46,204,113,0.25)');
      expect(rankColor(1, 0)).toBe('rgb(127,140,141)');
      expect(rankColor(0, 100)).toBe('rgb(127,140,141)');
      expect(rankColor(1, 100)).toBe('rgb(48,202,112)');
    });

    test('formats max-score colors', () => {
      expect(maxScoreColor(0)).toBe('rgb(220,40,40)');
      expect(maxScoreColor(100)).toBe('rgb(34,139,34)');
      expect(maxScoreColor(200)).toBe('rgb(34,139,34)');
    });
  });

  describe('formatRatingValue', () => {
    test('formats invalid, small, fractional, and whole ratings', () => {
      expect(formatRatingValue(Number.NaN)).toBe('N/A');
      expect(formatRatingValue(0.01)).toBe('0.01');
      expect(formatRatingValue(0)).toBe('0.0');
      expect(formatRatingValue(-0.0012)).toBe('-0.0012');
      expect(formatRatingValue(0.52)).toBe('0.52');
      expect(formatRatingValue(42.34)).toBe('42.3');
    });
  });

  describe('calculateScoreWidth', () => {
    test('returns ch width for longest formatted score', () => {
      const scores = [{ score: 100 }, { score: 1000 }, { score: 999999 }];
      const result = calculateScoreWidth(scores);
      expect(result).toMatch(/^\d+ch$/);
    });

    test('returns 1ch minimum for empty array', () => {
      expect(calculateScoreWidth([])).toBe('1ch');
    });
  });
});
