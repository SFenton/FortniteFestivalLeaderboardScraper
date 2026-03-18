/**
 * Additional branch coverage tests for hooks that are close to 95%.
 * Targets specific uncovered branches: null coalescences, edge cases.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';

// --- useVersions: branch where globals ARE defined ---
describe('useVersions branches', () => {
  it('returns version strings (from vite define or fallback)', async () => {
    const { APP_VERSION, CORE_VERSION } = await import('../../hooks/data/useVersions');
    // In test env, vite defines these from package.json
    expect(typeof APP_VERSION).toBe('string');
    expect(typeof CORE_VERSION).toBe('string');
    expect(APP_VERSION.length).toBeGreaterThan(0);
    expect(CORE_VERSION.length).toBeGreaterThan(0);
  });
});

// --- useChartData: uncovered branch at line 32 ---
import { useChartData } from '../../hooks/chart/useChartData';

vi.mock('../../api/client', () => ({
  api: {
    getPlayerHistory: vi.fn().mockResolvedValue({ accountId: 'test', count: 0, history: [] }),
    getSongs: vi.fn().mockResolvedValue({ songs: [], count: 0, currentSeason: 5 }),
    getSyncStatus: vi.fn().mockResolvedValue({ accountId: 'test', isTracked: false, backfill: null, historyRecon: null }),
    trackPlayer: vi.fn().mockResolvedValue({}),
    searchAccounts: vi.fn().mockResolvedValue({ results: [] }),
    getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
    getPlayer: vi.fn().mockResolvedValue({ accountId: 'test', displayName: 'Test', totalScores: 0, scores: [] }),
    getLeaderboard: vi.fn().mockResolvedValue({ songId: 'test', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: vi.fn().mockResolvedValue({ songId: 'test', instruments: [] }),
    getPlayerStats: vi.fn().mockResolvedValue({ accountId: 'test', stats: [] }),
  },
}));

import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';

function createQCWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

describe('useChartData additional branches', () => {
  it('returns empty chart data for undefined accountId', () => {
    const { result } = renderHook(
      () => useChartData(undefined as unknown as string, 'song-1', 'Solo_Guitar'),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData).toEqual([]);
    expect(result.current.loading).toBe(false);
  });

  it('returns empty chart data for undefined songId', () => {
    const { result } = renderHook(
      () => useChartData('acc-1', undefined as unknown as string, 'Solo_Guitar'),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData).toEqual([]);
  });
});

// --- useScrollFade: uncovered lines 106-107 ---
import { useScrollFade } from '../../hooks/ui/useScrollFade';

describe('useScrollFade additional branches', () => {
  it('handles null scrollRef', () => {
    const scrollRef = { current: null };
    const { result } = renderHook(() => useScrollFade(scrollRef, { current: null }));
    // Should not throw when called with null ref
    expect(typeof result.current).toBe('function');
    result.current(); // invoke without error
  });
});

// --- useHeaderCollapse: uncovered branch 28 ---
import { useHeaderCollapse } from '../../hooks/ui/useHeaderCollapse';

describe('useHeaderCollapse additional branches', () => {
  it('returns false when ref is null', () => {
    const ref = { current: null };
    const { result } = renderHook(() => useHeaderCollapse(ref));
    // useHeaderCollapse returns collapsed boolean (or a tuple first element)
    const collapsed = Array.isArray(result.current) ? result.current[0] : result.current;
    expect(collapsed).toBe(false);
  });
});

// --- useTrackedPlayer: uncovered branch 19 (empty displayName) ---
import { useTrackedPlayer } from '../../hooks/data/useTrackedPlayer';

describe('useTrackedPlayer additional branches', () => {
  beforeEach(() => { localStorage.clear(); });

  it('normalizes empty displayName to Unknown User', () => {
    const { result } = renderHook(() => useTrackedPlayer());
    act(() => { result.current.setPlayer({ accountId: 'a1', displayName: '' }); });
    expect(result.current.player?.displayName).toBe('Unknown User');
  });

  it('handles corrupt localStorage gracefully', () => {
    localStorage.setItem('fst:trackedPlayer', '{corrupt');
    const { result } = renderHook(() => useTrackedPlayer());
    expect(result.current.player).toBeNull();
  });
});
