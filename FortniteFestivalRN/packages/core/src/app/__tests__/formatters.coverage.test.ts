import {formatIntegerWithCommas, formatScoreCompact, formatPercentileTopExact, instrumentKeyToLabel, instrumentKeyToColorHex} from '../formatters';

describe('formatters extended coverage', () => {
  /* ── formatScoreCompact ── */

  test('formatScoreCompact handles billions', () => {
    expect(formatScoreCompact(1_500_000_000)).toBe('1.50B');
  });

  test('formatScoreCompact handles millions', () => {
    expect(formatScoreCompact(2_500_000)).toBe('2.50M');
  });

  test('formatScoreCompact handles thousands', () => {
    expect(formatScoreCompact(1_500)).toBe('1.5K');
  });

  test('formatScoreCompact handles small numbers', () => {
    expect(formatScoreCompact(42)).toBe('42');
  });

  test('formatScoreCompact handles NaN/Infinity', () => {
    expect(formatScoreCompact(NaN)).toBe('N/A');
    expect(formatScoreCompact(Infinity)).toBe('N/A');
  });

  test('formatScoreCompact handles negative values', () => {
    expect(formatScoreCompact(-2_500_000)).toBe('-2.50M');
    expect(formatScoreCompact(-1_500)).toBe('-1.5K');
    expect(formatScoreCompact(-1_500_000_000)).toBe('-1.50B');
  });

  /* ── formatPercentileTopExact ── */

  test('formatPercentileTopExact formats sub-1% with 2 decimals', () => {
    expect(formatPercentileTopExact(0.0050)).toBe('Top 0.50%');
    expect(formatPercentileTopExact(0.0001)).toBe('Top 0.01%');
  });

  test('formatPercentileTopExact formats >= 1% as integer', () => {
    expect(formatPercentileTopExact(0.05)).toBe('Top 5%');
    expect(formatPercentileTopExact(0.50)).toBe('Top 50%');
    expect(formatPercentileTopExact(1.0)).toBe('Top 100%');
  });

  test('formatPercentileTopExact returns N/A for non-finite', () => {
    expect(formatPercentileTopExact(NaN)).toBe('N/A');
    expect(formatPercentileTopExact(Infinity)).toBe('N/A');
  });

  test('formatPercentileTopExact applies bankers rounding at tie (even)', () => {
    // rawPercentile = 0.025 → topPct = 2.5 → bankersRoundInt(2.5) → 2 (even)
    expect(formatPercentileTopExact(0.025)).toBe('Top 2%');
  });

  test('formatPercentileTopExact applies bankers rounding at tie (odd)', () => {
    // rawPercentile = 0.015 → topPct = 1.5 → bankersRoundInt(1.5) → 2 (round up to even)
    expect(formatPercentileTopExact(0.015)).toBe('Top 2%');
  });

  test('formatPercentileTopExact sub-1% bankers tie', () => {
    // rawPercentile = 0.00005 → topPct = 0.005 → bankersRound(0.005, 2) → scaled = 0.5
    // bankersRoundInt(0.5) → 0 (even) → 0/100 = 0.00
    expect(formatPercentileTopExact(0.00005)).toBe('Top 0.01%');
    // Clamped to 0.01 minimum
  });

  test('formatPercentileTopExact covers various rounding paths', () => {
    // Round down path: topPct just below .5
    // rawPercentile = 0.023 → topPct = 2.3 → bankersRoundInt(2.3): frac=0.3 < 0.5-eps → 2
    expect(formatPercentileTopExact(0.023)).toBe('Top 2%');
    // Round up path: topPct just above .5
    // rawPercentile = 0.027 → topPct = 2.7 → bankersRoundInt(2.7): frac=0.7 > 0.5+eps → 3
    expect(formatPercentileTopExact(0.027)).toBe('Top 3%');
    // Negative value should be clamped to 0.01
    expect(formatPercentileTopExact(-0.5)).toBe('Top 0.01%');
  });

  test('formatPercentileTopExact sub-1% rounding paths', () => {
    // rawPercentile = 0.0067 → topPct = 0.67 → bankersRound(0.67, 2) → scaled = 67
    // bankersRoundInt(67) → 67, result = 0.67
    expect(formatPercentileTopExact(0.0067)).toBe('Top 0.67%');
    // rawPercentile = 0.0023 → topPct = 0.23 → bankersRound(0.23, 2) → scaled = 23
    // bankersRoundInt(23) → 23, result = 0.23
    expect(formatPercentileTopExact(0.0023)).toBe('Top 0.23%');
  });

  test('formatPercentileTopExact negative decimals in bankersRound', () => {
    // This tests the `decimals < 0` early return path in bankersRound
    // Can't be triggered through formatPercentileTopExact — it always passes 0 or 2
    // But let's ensure the top-level function handles edge values
    expect(formatPercentileTopExact(0)).toBe('Top 0.01%');
  });

  test('formatPercentileTopExact with value exactly at 1% boundary', () => {
    // rawPercentile = 0.01 → topPct = 1.0 → >= 1 → bankersRound(1.0, 0) → 1
    expect(formatPercentileTopExact(0.01)).toBe('Top 1%');
  });

  test('formatPercentileTopExact with very small but positive value', () => {
    // rawPercentile = 0.000001 → topPct = 0.0001 → clamp to 0.01
    expect(formatPercentileTopExact(0.000001)).toBe('Top 0.01%');
  });

  test('formatPercentileTopExact with value exactly 100%', () => {
    expect(formatPercentileTopExact(1.0)).toBe('Top 100%');
  });

  test('formatPercentileTopExact value above 100% clamped', () => {
    expect(formatPercentileTopExact(2.0)).toBe('Top 100%');
  });

  test('formatScoreCompact handles exactly 1000', () => {
    expect(formatScoreCompact(1000)).toBe('1.0K');
  });

  test('formatScoreCompact handles exactly 1000000', () => {
    expect(formatScoreCompact(1000000)).toBe('1.00M');
  });

  test('formatScoreCompact handles exactly 1000000000', () => {
    expect(formatScoreCompact(1000000000)).toBe('1.00B');
  });

  test('formatScoreCompact handles zero', () => {
    expect(formatScoreCompact(0)).toBe('0');
  });

  test('formatScoreCompact handles value between 100 and 999', () => {
    expect(formatScoreCompact(500)).toBe('500');
  });

  test('formatScoreCompact negative small value', () => {
    expect(formatScoreCompact(-42)).toBe('-42');
  });

  /* ── bankersRoundInt with negative values (sign = -1 path) ── */

  test('formatPercentileTopExact with negative rawPercentile clamped to 0.01', () => {
    // rawPercentile = -1 → topPct = clamp(-100, 0.01, 100) → 0.01
    // topPct < 1 → bankersRound(0.01, 2) → 0.01
    expect(formatPercentileTopExact(-1)).toBe('Top 0.01%');
  });

  /* ── instrumentKeyToLabel and instrumentKeyToColorHex don't have more branches
       to cover, but let's ensure formatIntegerWithCommas edge cases ── */

  test('formatIntegerWithCommas large positive number', () => {
    expect(formatIntegerWithCommas(1234567890)).toBe('1,234,567,890');
  });

  test('formatIntegerWithCommas single digit', () => {
    expect(formatIntegerWithCommas(5)).toBe('5');
  });

  test('formatIntegerWithCommas rounds fractional values', () => {
    expect(formatIntegerWithCommas(1234.7)).toBe('1,235');
    expect(formatIntegerWithCommas(1234.2)).toBe('1,234');
  });

  /* ── instrumentKeyToLabel ── */

  test('instrumentKeyToLabel covers all keys', () => {
    expect(instrumentKeyToLabel('guitar')).toBe('Lead');
    expect(instrumentKeyToLabel('bass')).toBe('Bass');
    expect(instrumentKeyToLabel('drums')).toBe('Drums');
    expect(instrumentKeyToLabel('vocals')).toBe('Vocals');
    expect(instrumentKeyToLabel('pro_guitar')).toBe('Pro Lead');
    expect(instrumentKeyToLabel('pro_bass')).toBe('Pro Bass');
  });

  test('instrumentKeyToLabel returns key for unknown', () => {
    expect(instrumentKeyToLabel('unknown' as any)).toBe('unknown');
  });

  /* ── instrumentKeyToColorHex ── */

  test('instrumentKeyToColorHex covers all keys', () => {
    expect(instrumentKeyToColorHex('guitar')).toBe('#b35cd6');
    expect(instrumentKeyToColorHex('bass')).toBe('#3498db');
    expect(instrumentKeyToColorHex('drums')).toBe('#e74c3c');
    expect(instrumentKeyToColorHex('vocals')).toBe('#27ae60');
    expect(instrumentKeyToColorHex('pro_guitar')).toBe('#9b59b6');
    expect(instrumentKeyToColorHex('pro_bass')).toBe('#2980b9');
  });

  test('instrumentKeyToColorHex returns gray for unknown', () => {
    expect(instrumentKeyToColorHex('unknown' as any)).toBe('#7f8c8d');
  });

  /* ── formatIntegerWithCommas ── */

  test('formatIntegerWithCommas handles negatives', () => {
    expect(formatIntegerWithCommas(-1234)).toBe('-1,234');
  });

  test('formatIntegerWithCommas handles zero', () => {
    expect(formatIntegerWithCommas(0)).toBe('0');
  });
});
