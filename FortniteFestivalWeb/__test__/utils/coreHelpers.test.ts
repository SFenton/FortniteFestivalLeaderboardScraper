import { describe, it, expect } from 'vitest';
import { Keys } from '@festival/core';
import { MAX_DISPLAY_STARS, GOLD_STARS_THRESHOLD, displayStarCount } from '@festival/core';
import { PercentileTier, PERCENTILE_THRESHOLDS } from '@festival/core';

describe('Keys', () => {
  it('has expected key constants', () => {
    expect(Keys.Escape).toBe('Escape');
    expect(Keys.Enter).toBe('Enter');
    expect(Keys.ArrowDown).toBe('ArrowDown');
    expect(Keys.ArrowUp).toBe('ArrowUp');
    expect(Keys.Tab).toBe('Tab');
    expect(Keys.Space).toBe(' ');
  });
});

describe('Stars constants', () => {
  it('MAX_DISPLAY_STARS is 5', () => {
    expect(MAX_DISPLAY_STARS).toBe(5);
  });

  it('GOLD_STARS_THRESHOLD is 6', () => {
    expect(GOLD_STARS_THRESHOLD).toBe(6);
  });

  it('displayStarCount returns 5 for gold (6+)', () => {
    expect(displayStarCount(6)).toBe(5);
    expect(displayStarCount(7)).toBe(5);
  });

  it('displayStarCount returns count for 1-5', () => {
    expect(displayStarCount(1)).toBe(1);
    expect(displayStarCount(3)).toBe(3);
    expect(displayStarCount(5)).toBe(5);
  });

  it('displayStarCount clamps below 1', () => {
    expect(displayStarCount(0)).toBe(1);
    expect(displayStarCount(-1)).toBe(1);
  });
});

describe('PercentileTier', () => {
  it('has expected display strings', () => {
    expect(PercentileTier.Top1).toBe('Top 1%');
    expect(PercentileTier.Top5).toBe('Top 5%');
    expect(PercentileTier.Top10).toBe('Top 10%');
  });
});

describe('PERCENTILE_THRESHOLDS', () => {
  it('starts at 1 and ends at 100', () => {
    expect(PERCENTILE_THRESHOLDS[0]!).toBe(1);
    expect(PERCENTILE_THRESHOLDS[PERCENTILE_THRESHOLDS.length - 1]!).toBe(100);
  });

  it('is sorted ascending', () => {
    for (let i = 1; i < PERCENTILE_THRESHOLDS.length; i++) {
      expect(PERCENTILE_THRESHOLDS[i]!).toBeGreaterThan(PERCENTILE_THRESHOLDS[i - 1]!);
    }
  });
});
