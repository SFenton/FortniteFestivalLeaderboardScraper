import { describe, it, expect, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useChartData } from '../../../hooks/chart/useChartData';
import { createTestQueryClient } from '../../helpers/TestProviders';
import { MOCK_HISTORY_ENTRIES } from '../../helpers/apiMocks';

vi.mock('../../../api/client', () => ({
  api: {
    getPlayerHistory: vi.fn().mockResolvedValue({
      accountId: 'test-player-1', count: 3,
      history: [
        { songId: 'song-1', instrument: 'Solo_Guitar', newScore: 100000, newRank: 5, changedAt: '2025-01-15T10:00:00Z', scoreAchievedAt: '2025-01-15T10:00:00Z', accuracy: 950000, isFullCombo: false, stars: 5, season: 5 },
        { songId: 'song-1', instrument: 'Solo_Guitar', newScore: 120000, newRank: 3, changedAt: '2025-02-15T10:00:00Z', scoreAchievedAt: '2025-02-15T10:00:00Z', accuracy: 970000, isFullCombo: true, stars: 6, season: 5 },
        { songId: 'song-1', instrument: 'Solo_Bass', newScore: 80000, newRank: 10, changedAt: '2025-01-20T10:00:00Z', scoreAchievedAt: '2025-01-20T10:00:00Z', accuracy: 900000, isFullCombo: false, stars: 4, season: 5 },
      ],
    }),
  },
}));

function createQCWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

function hist(o: Partial<Record<string, unknown>> = {}) {
  return { songId: 's1', instrument: 'Solo_Guitar', newScore: 100000, newRank: 5, changedAt: '2025-01-15T10:00:00Z', ...o } as any;
}

function makeHistory(overrides: Partial<Record<string, unknown>> = {}) {
  return {
    songId: 'song-1', instrument: 'Solo_Guitar', newScore: 100000, newRank: 5,
    accuracy: 950000, isFullCombo: false, stars: 5, season: 5,
    changedAt: '2025-01-15T10:00:00Z', scoreAchievedAt: '2025-01-15T10:00:00Z',
    ...overrides,
  } as any;
}

describe('useChartData branches', () => {
  it('scoreAchievedAt=undefined → changedAt fallback', () => {
    const history = [hist({ instrument: 'Solo_Guitar', scoreAchievedAt: undefined, changedAt: '2025-06-15T10:00:00Z' })];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: createQCWrapper() });
    expect(result.current.chartData[0]!.dateLabel).toContain('6/15');
  });

  it('accuracy=undefined → 0 fallback', () => {
    const history = [hist({ instrument: 'Solo_Guitar', accuracy: undefined })];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: createQCWrapper() });
    expect(result.current.chartData[0]!.accuracy).toBe(0);
  });

  it('stars=undefined and season=undefined → undefined in output', () => {
    const history = [hist({ instrument: 'Solo_Guitar', stars: undefined, season: undefined })];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: createQCWrapper() });
    expect(result.current.chartData[0]!.stars).toBeUndefined();
    expect(result.current.chartData[0]!.season).toBeUndefined();
  });

  it('isFullCombo=undefined → false fallback', () => {
    const history = [hist({ instrument: 'Solo_Guitar', isFullCombo: undefined })];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: createQCWrapper() });
    expect(result.current.chartData[0]!.isFullCombo).toBe(false);
  });
});

describe('useChartData — undefined accountId/songId', () => {
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

describe('useChartData with historyProp', () => {
  it('returns chart data from historyProp without fetching', () => {
    const history = [
      makeHistory({ instrument: 'Solo_Guitar', newScore: 150000 }),
      makeHistory({ instrument: 'Solo_Bass', newScore: 80000 }),
    ];
    const { result } = renderHook(
      () => useChartData('acc-1', 'song-1', 'Solo_Guitar', history),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData.length).toBe(1);
    expect(result.current.chartData[0]!.score).toBe(150000);
    expect(result.current.loading).toBe(false);
  });

  it('filters by instrument', () => {
    const history = [
      makeHistory({ instrument: 'Solo_Guitar' }),
      makeHistory({ instrument: 'Solo_Bass' }),
      makeHistory({ instrument: 'Solo_Guitar', scoreAchievedAt: '2025-02-01T10:00:00Z' }),
    ];
    const { result } = renderHook(
      () => useChartData('acc-1', 'song-1', 'Solo_Guitar', history),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData.length).toBe(2);
  });

  it('computes instrumentCounts correctly', () => {
    const history = [
      makeHistory({ instrument: 'Solo_Guitar' }),
      makeHistory({ instrument: 'Solo_Guitar', scoreAchievedAt: '2025-02-01T10:00:00Z' }),
      makeHistory({ instrument: 'Solo_Bass' }),
    ];
    const { result } = renderHook(
      () => useChartData('acc-1', 'song-1', 'Solo_Guitar', history),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.instrumentCounts['Solo_Guitar']).toBe(2);
    expect(result.current.instrumentCounts['Solo_Bass']).toBe(1);
  });

  it('sorts chart data by date ascending', () => {
    const history = [
      makeHistory({ instrument: 'Solo_Guitar', scoreAchievedAt: '2025-03-01T10:00:00Z', newScore: 200000 }),
      makeHistory({ instrument: 'Solo_Guitar', scoreAchievedAt: '2025-01-01T10:00:00Z', newScore: 100000 }),
    ];
    const { result } = renderHook(
      () => useChartData('acc-1', 'song-1', 'Solo_Guitar', history),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData[0]!.score).toBe(100000);
    expect(result.current.chartData[1]!.score).toBe(200000);
  });

  it('handles null accuracy with 0 fallback', () => {
    const history = [makeHistory({ instrument: 'Solo_Guitar', accuracy: undefined })];
    const { result } = renderHook(
      () => useChartData('acc-1', 'song-1', 'Solo_Guitar', history),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData[0]!.accuracy).toBe(0);
  });

  it('returns loading=false when historyProp is provided', () => {
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, []),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.loading).toBe(false);
  });
});

describe('useChartData — memo branches', () => {
  const HISTORY = [
    { songId: 's1', instrument: 'Solo_Guitar', newScore: 100, newRank: 1, changedAt: '2025-01-01T00:00:00Z', scoreAchievedAt: '2025-01-01T12:00:00Z', accuracy: 950000, isFullCombo: true, stars: 5, season: 3 },
    { songId: 's1', instrument: 'Solo_Guitar', newScore: 200, newRank: 1, changedAt: '2025-01-02T00:00:00Z', scoreAchievedAt: null, accuracy: null, isFullCombo: null, stars: null, season: null },
    { songId: 's1', instrument: 'Solo_Bass', newScore: 50, newRank: 2, changedAt: '2025-01-01T00:00:00Z', scoreAchievedAt: '2025-01-01T00:00:00Z', accuracy: 800000, isFullCombo: false, stars: 3, season: 2 },
    { songId: 's1', instrument: 'Solo_Guitar', newScore: 300, newRank: 1, changedAt: '2025-01-01T06:00:00Z', scoreAchievedAt: '2025-01-01T06:00:00Z', accuracy: 990000, isFullCombo: true, stars: 6, season: 4 },
  ] as any;

  it('transforms history into chartData with null accuracy → 0', () => {
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, HISTORY),
      { wrapper: createQCWrapper() },
    );
    const guiPoints = result.current.chartData;
    expect(guiPoints).toHaveLength(3);
    const nullAccEntry = guiPoints.find(p => p.score === 200);
    expect(nullAccEntry!.accuracy).toBe(0);
    expect(nullAccEntry!.isFullCombo).toBe(false);
    expect(nullAccEntry!.stars).toBeUndefined();
    expect(nullAccEntry!.season).toBeUndefined();
    const realAccEntry = guiPoints.find(p => p.score === 100);
    expect(realAccEntry!.accuracy).toBeGreaterThan(0);
    expect(realAccEntry!.isFullCombo).toBe(true);
    expect(realAccEntry!.stars).toBe(5);
  });

  it('builds instrumentCounts across all instruments', () => {
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, HISTORY),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.instrumentCounts).toEqual({ Solo_Guitar: 3, Solo_Bass: 1 });
  });

  it('uses changedAt fallback when scoreAchievedAt is null', () => {
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, HISTORY),
      { wrapper: createQCWrapper() },
    );
    const changedAtEntry = result.current.chartData.find(p => p.score === 200);
    expect(changedAtEntry!.date).toContain('2025-01-02');
  });

  it('handles same-day entries', () => {
    const sameDayHistory = [
      { songId: 's1', instrument: 'Solo_Guitar', newScore: 100, newRank: 1, changedAt: '2025-01-01T10:00:00Z', scoreAchievedAt: '2025-01-01T10:00:00Z', accuracy: 950000, isFullCombo: true, stars: 5, season: 3 },
      { songId: 's1', instrument: 'Solo_Guitar', newScore: 200, newRank: 1, changedAt: '2025-01-01T14:00:00Z', scoreAchievedAt: '2025-01-01T14:00:00Z', accuracy: 960000, isFullCombo: true, stars: 6, season: 3 },
    ];
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, sameDayHistory as any),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData[0]!.dateLabel).toBe(result.current.chartData[1]!.dateLabel);
  });
});

describe('useChartData — queryFn', () => {
  it('fetches history via queryFn when no historyProp', async () => {
    const wrapper = ({ children }: { children: ReactNode }) => {
      const qc = createTestQueryClient();
      return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
    };
    const { result } = renderHook(
      () => useChartData('test-player-1', 'song-1', 'Solo_Guitar'),
      { wrapper },
    );
    await waitFor(() => expect(result.current.loading).toBe(false));
    expect(result.current.chartData.length).toBeGreaterThan(0);
    expect(result.current.chartData[0]).toHaveProperty('score');
  });

  it('uses historyProp when provided', () => {
    const wrapper = ({ children }: { children: ReactNode }) => {
      const qc = createTestQueryClient();
      return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
    };
    const { result } = renderHook(
      () => useChartData('test-player-1', 'song-1', 'Solo_Guitar', MOCK_HISTORY_ENTRIES),
      { wrapper },
    );
    expect(result.current.loading).toBe(false);
    expect(result.current.chartData.length).toBe(3);
  });

  it('returns instrumentCounts', () => {
    const wrapper = ({ children }: { children: ReactNode }) => {
      const qc = createTestQueryClient();
      return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
    };
    const { result } = renderHook(
      () => useChartData('test-player-1', 'song-1', 'Solo_Guitar', MOCK_HISTORY_ENTRIES),
      { wrapper },
    );
    expect(result.current.instrumentCounts).toBeDefined();
    expect(result.current.instrumentCounts.Solo_Guitar).toBe(3);
  });
});

describe('useChartData additional ?? branches', () => {
  it('multiple entries with undefined scoreAchievedAt', () => {
    const history = [
      hist({ instrument: 'Solo_Guitar', scoreAchievedAt: undefined, changedAt: '2025-01-01T00:00:00Z', newScore: 100000 }),
      hist({ instrument: 'Solo_Guitar', scoreAchievedAt: '2025-02-01T00:00:00Z', newScore: 200000 }),
    ];
    const { result } = renderHook(() => useChartData('a1', 's1', 'Solo_Guitar', history), { wrapper: createQCWrapper() });
    expect(result.current.chartData.length).toBe(2);
    expect(result.current.chartData[0]!.score).toBe(100000);
  });
});
