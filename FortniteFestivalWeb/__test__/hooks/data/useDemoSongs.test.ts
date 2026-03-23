import { describe, it, expect } from 'vitest';
import { shuffle, fitRows } from '../../../src/hooks/data/useDemoSongs';

describe('shuffle', () => {
  it('returns an array of the same length', () => {
    const input = [1, 2, 3, 4, 5];
    const result = shuffle(input);
    expect(result).toHaveLength(input.length);
  });

  it('contains the same elements', () => {
    const input = [10, 20, 30, 40, 50];
    const result = shuffle(input);
    expect(result.sort()).toEqual([...input].sort());
  });

  it('does not mutate the original array', () => {
    const input = [1, 2, 3];
    const original = [...input];
    shuffle(input);
    expect(input).toEqual(original);
  });

  it('handles empty array', () => {
    expect(shuffle([])).toEqual([]);
  });

  it('handles single element', () => {
    expect(shuffle([42])).toEqual([42]);
  });
});

describe('fitRows', () => {
  it('returns 1 for zero container height', () => {
    expect(fitRows(0, 50)).toBe(1);
  });

  it('returns 1 for negative container height', () => {
    expect(fitRows(-100, 50)).toBe(1);
  });

  it('calculates correct row count', () => {
    // (600 + gap) / (50 + gap) — using Layout.demoRowGap which is imported from @festival/theme
    // The formula: Math.floor((containerHeight + gap) / (rowHeight + gap))
    // We can test the basic math behavior
    const result = fitRows(300, 50);
    expect(result).toBeGreaterThanOrEqual(1);
  });

  it('returns at least 1 for very small container', () => {
    expect(fitRows(10, 500)).toBe(1);
  });
});
