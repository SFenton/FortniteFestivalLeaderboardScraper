import { describe, it, expect } from 'vitest';
import { accuracyColor, calculateScoreWidth } from '@festival/core';

describe('accuracyColor', () => {
  it('returns red at 0%', () => {
    expect(accuracyColor(0)).toBe('rgb(220,40,40)');
  });

  it('returns green at 100%', () => {
    expect(accuracyColor(100)).toBe('rgb(46,204,113)');
  });

  it('returns interpolated color at 50%', () => {
    expect(accuracyColor(50)).toBe('rgb(133,122,77)');
  });

  it('clamps below 0', () => {
    expect(accuracyColor(-10)).toBe('rgb(220,40,40)');
  });

  it('clamps above 100', () => {
    expect(accuracyColor(150)).toBe('rgb(46,204,113)');
  });
});

describe('calculateScoreWidth', () => {
  it('returns ch width for longest formatted score', () => {
    const scores = [{ score: 100 }, { score: 1000 }, { score: 999999 }];
    const result = calculateScoreWidth(scores);
    expect(result).toMatch(/^\d+ch$/);
  });

  it('returns 1ch for empty array', () => {
    expect(calculateScoreWidth([])).toBe('1ch');
  });
});
