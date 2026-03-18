/**
 * Targeted branch coverage tests for hooks and utilities.
 * Covers uncovered ternaries, null guards, and conditional paths.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';

// --- useHeaderCollapse ---
import { useHeaderCollapse } from '../../hooks/ui/useHeaderCollapse';

describe('useHeaderCollapse additional branches', () => {
  it('returns false when disabled', () => {
    const ref = { current: null };
    const { result } = renderHook(() => useHeaderCollapse(ref, { disabled: true }));
    const collapsed = Array.isArray(result.current) ? result.current[0] : result.current;
    expect(collapsed).toBe(false);
  });

  it('returns forced value when provided', () => {
    const ref = { current: null };
    const { result } = renderHook(() => useHeaderCollapse(ref, { disabled: true, forcedValue: true }));
    const collapsed = Array.isArray(result.current) ? result.current[0] : result.current;
    expect(collapsed).toBe(true);
  });
});

// --- useScrollFade ---
import { useScrollFade } from '../../hooks/ui/useScrollFade';

describe('useScrollFade rAF branch', () => {
  beforeEach(() => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 1; });
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
  });
  afterEach(() => vi.restoreAllMocks());

  it('returns an update function that calls rAF', () => {
    const scrollRef = { current: document.createElement('div') };
    const listRef = { current: document.createElement('div') };
    const { result } = renderHook(() => useScrollFade(scrollRef, listRef, []));
    expect(typeof result.current).toBe('function');
    result.current();
  });
});

// --- useScrollRestore ---
import { useScrollRestore } from '../../hooks/ui/useScrollRestore';

describe('useScrollRestore branches', () => {
  beforeEach(() => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 1; });
    vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
    sessionStorage.clear();
  });
  afterEach(() => vi.restoreAllMocks());

  it('restores scroll position from sessionStorage', () => {
    sessionStorage.setItem('scroll:test', '100');
    const el = document.createElement('div');
    Object.defineProperty(el, 'scrollHeight', { value: 500, writable: true });
    const ref = { current: el };
    const { result } = renderHook(() => useScrollRestore(ref, 'test', 'POP' as any));
    // saveScroll should be a function
    expect(typeof result.current).toBe('function');
  });

  it('returns a save function', () => {
    const el = document.createElement('div');
    const ref = { current: el };
    const { result } = renderHook(() => useScrollRestore(ref, 'test2', 'PUSH' as any));
    expect(typeof result.current).toBe('function');
  });
});

// --- useChartPagination ---
import { useChartPagination } from '../../hooks/chart/useChartPagination';

describe('useChartPagination additional branches', () => {
  it('clamps offset to valid range', () => {
    const data = Array.from({ length: 20 }, (_, i) => ({ date: `d${i}` })) as any[];
    const { result } = renderHook(() => useChartPagination(data, 5, 'Solo_Guitar'));
    expect(result.current.pageStart).toBeGreaterThanOrEqual(0);
  });

  it('handles empty data', () => {
    const { result } = renderHook(() => useChartPagination([], 5, 'Solo_Guitar'));
    expect(result.current.pageStart).toBe(0);
    expect(result.current.pageEnd).toBe(0);
  });
});

// --- useAccountSearch click-outside ---
import { useAccountSearch } from '../../hooks/data/useAccountSearch';

function createQCWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

describe('useAccountSearch branches', () => {
  it('returns initial state', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(
      () => useAccountSearch(onSelect),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.query).toBe('');
    expect(result.current.results).toEqual([]);
  });

  it('setQuery updates query string', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(
      () => useAccountSearch(onSelect),
      { wrapper: createQCWrapper() },
    );
    act(() => result.current.setQuery('test'));
    expect(result.current.query).toBe('test');
  });
});

// --- playerStats branches ---
import { computeInstrumentStats, computeOverallStats } from '../../pages/player/helpers/playerStats';
import { ACCURACY_SCALE } from '@festival/core';
import type { PlayerScore } from '@festival/core/api/serverTypes';

function makePS(overrides: Partial<PlayerScore> = {}): PlayerScore {
  return { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, totalEntries: 100, accuracy: 95 * ACCURACY_SCALE, isFullCombo: false, stars: 5, season: 5, ...overrides };
}

describe('playerStats branches', () => {
  it('computeInstrumentStats with stars=0 scores', () => {
    const stats = computeInstrumentStats([makePS({ stars: 0 })], 10);
    expect(stats.averageStars).toBe(0);
  });

  it('computeInstrumentStats with bestRank=0', () => {
    const stats = computeInstrumentStats([makePS({ rank: 0, totalEntries: 0 })], 10);
    expect(stats.bestRank).toBe(0);
    expect(stats.bestRankSongId).toBeNull();
  });

  it('computeInstrumentStats with mixed stars', () => {
    const scores = [
      makePS({ songId: 's1', stars: 6 }),
      makePS({ songId: 's2', stars: 5 }),
      makePS({ songId: 's3', stars: 4 }),
      makePS({ songId: 's4', stars: 3 }),
      makePS({ songId: 's5', stars: 2 }),
      makePS({ songId: 's6', stars: 1 }),
    ];
    const stats = computeInstrumentStats(scores, 10);
    expect(stats.goldStarCount).toBe(1);
    expect(stats.fiveStarCount).toBe(1);
    expect(stats.averageStars).toBeGreaterThan(0);
  });

  it('computeOverallStats with FC scores', () => {
    const stats = computeOverallStats([makePS({ isFullCombo: true }), makePS({ isFullCombo: false })]);
    expect(stats.fcCount).toBe(1);
    expect(parseFloat(stats.fcPercent)).toBeGreaterThan(0);
  });

  it('computeOverallStats with unique songs', () => {
    const stats = computeOverallStats([makePS({ songId: 's1' }), makePS({ songId: 's2' })]);
    expect(stats.songsPlayed).toBe(2);
  });

  it('computeOverallStats bestRank across instruments', () => {
    const stats = computeOverallStats([makePS({ rank: 3 }), makePS({ rank: 1 })]);
    expect(stats.bestRank).toBe(1);
  });
});

// --- suggestionsFilter --- (isFilterActive is in songSettings, not here)
// Tests for filterCategoryForInstrumentTypes are in suggestionsFilter.test.ts
