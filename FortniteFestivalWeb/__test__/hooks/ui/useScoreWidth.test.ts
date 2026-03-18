import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useScoreWidth, calculateScoreWidth } from '../../../src/hooks/ui/useScoreWidth';

describe('useScoreWidth', () => {
  it('returns 1ch for empty array', () => {
    const { result } = renderHook(() => useScoreWidth([]));
    expect(result.current).toBe('1ch');
  });

  it('returns width based on longest score', () => {
    const { result } = renderHook(() => useScoreWidth([1000, 999999, 50]));
    // 999,999 = 7 chars with commas
    expect(result.current).toBe('7ch');
  });

  it('handles single-digit scores', () => {
    const { result } = renderHook(() => useScoreWidth([5]));
    expect(result.current).toBe('1ch');
  });
});

describe('calculateScoreWidth', () => {
  it('returns 1ch for empty inputs', () => {
    expect(calculateScoreWidth([])).toBe('1ch');
  });

  it('computes width across multiple arrays', () => {
    const result = calculateScoreWidth([100], [1000000]);
    // 1,000,000 = 9 chars
    expect(result).toBe('9ch');
  });
});
