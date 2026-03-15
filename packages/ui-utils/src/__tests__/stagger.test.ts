import { describe, it, expect } from 'vitest';
import { staggerDelay, estimateVisibleCount } from '../stagger';

describe('staggerDelay', () => {
  it('returns delay for items within maxItems', () => {
    expect(staggerDelay(0, 125, 10)).toBe(125);
    expect(staggerDelay(1, 125, 10)).toBe(250);
    expect(staggerDelay(9, 125, 10)).toBe(1250);
  });

  it('returns undefined for items beyond maxItems', () => {
    expect(staggerDelay(10, 125, 10)).toBeUndefined();
    expect(staggerDelay(100, 125, 10)).toBeUndefined();
  });

  it('works with interval 0', () => {
    expect(staggerDelay(5, 0, 10)).toBe(0);
  });

  it('works with maxItems 0', () => {
    expect(staggerDelay(0, 125, 0)).toBeUndefined();
  });
});

describe('estimateVisibleCount', () => {
  it('calculates count based on viewport height', () => {
    // Default window.innerHeight in jsdom is typically 768
    const count = estimateVisibleCount(100);
    expect(count).toBeGreaterThan(0);
    expect(count).toBeLessThan(50); // sanity check
  });

  it('returns at least 2 for large items', () => {
    const count = estimateVisibleCount(10000);
    expect(count).toBeGreaterThanOrEqual(2); // ceil(height/10000) + 1
  });

  it('adds 1 buffer for partial visibility', () => {
    // For exactly fitting items, should still add 1
    const height = typeof window !== 'undefined' ? window.innerHeight : 900;
    const exactFit = estimateVisibleCount(height);
    expect(exactFit).toBe(2); // ceil(1) + 1 = 2
  });
});
