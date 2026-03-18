import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useLeaderboardColumns } from '../../hooks/ui/useLeaderboardColumns';

let matchMediaResults: Record<string, boolean> = {};

beforeEach(() => {
  matchMediaResults = {};
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: matchMediaResults[query] ?? false,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe('useLeaderboardColumns', () => {
  it('returns all false on narrow viewports', () => {
    const { result } = renderHook(() => useLeaderboardColumns());
    expect(result.current.showAccuracy).toBe(false);
    expect(result.current.showSeason).toBe(false);
    expect(result.current.showStars).toBe(false);
  });

  it('returns all true when all breakpoints match', () => {
    // Set all queries to match
    matchMediaResults = {};
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: true,
        media: query,
        onchange: null,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    });

    const { result } = renderHook(() => useLeaderboardColumns());
    expect(result.current.showAccuracy).toBe(true);
    expect(result.current.showSeason).toBe(true);
    expect(result.current.showStars).toBe(true);
  });
});
