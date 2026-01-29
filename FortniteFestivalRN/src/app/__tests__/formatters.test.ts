import {
  formatPercentileTopExact,
  formatScoreCompact,
  instrumentKeyToColorHex,
  instrumentKeyToLabel,
} from '../format/formatters';

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

  describe('instrument presentation', () => {
    test('maps keys to labels', () => {
      expect(instrumentKeyToLabel('guitar')).toBe('Lead');
      expect(instrumentKeyToLabel('pro_guitar')).toBe('Pro Guitar');
    });

    test('maps keys to colors', () => {
      expect(instrumentKeyToColorHex('guitar')).toBe('#b35cd6');
      expect(instrumentKeyToColorHex('vocals')).toBe('#27ae60');
    });
  });
});
