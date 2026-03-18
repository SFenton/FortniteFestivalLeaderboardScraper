/**
 * Additional coverage tests targeting specific uncovered branches and functions
 * across multiple hooks and utilities.
 */
import { describe, it, expect, vi } from 'vitest';
import { renderHook } from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ServerScoreHistoryEntry } from '@festival/core/api/serverTypes';

vi.mock('../../api/client', () => ({
  api: {
    getPlayerHistory: vi.fn().mockResolvedValue({ accountId: 'test', count: 0, history: [] }),
  },
}));

function createQCWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

// --- useChartData: test with historyProp to cover the local-data path ---
import { useChartData } from '../../hooks/chart/useChartData';

function makeHistory(overrides: Partial<ServerScoreHistoryEntry> = {}): ServerScoreHistoryEntry {
  return {
    songId: 'song-1',
    instrument: 'Solo_Guitar',
    newScore: 100000,
    newRank: 5,
    accuracy: 950000,
    isFullCombo: false,
    stars: 5,
    season: 5,
    changedAt: '2025-01-15T10:00:00Z',
    scoreAchievedAt: '2025-01-15T10:00:00Z',
    ...overrides,
  };
}

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
    const history = [
      makeHistory({ instrument: 'Solo_Guitar', accuracy: undefined }),
    ];
    const { result } = renderHook(
      () => useChartData('acc-1', 'song-1', 'Solo_Guitar', history),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData[0]!.accuracy).toBe(0);
  });

  it('handles entries with same date (day deduplication)', () => {
    const history = [
      makeHistory({ instrument: 'Solo_Guitar', scoreAchievedAt: '2025-01-15T08:00:00Z', newScore: 100000 }),
      makeHistory({ instrument: 'Solo_Guitar', scoreAchievedAt: '2025-01-15T12:00:00Z', newScore: 110000 }),
    ];
    const { result } = renderHook(
      () => useChartData('acc-1', 'song-1', 'Solo_Guitar', history),
      { wrapper: createQCWrapper() },
    );
    expect(result.current.chartData.length).toBe(2);
    // Both should have the same dateLabel
    expect(result.current.chartData[0]!.dateLabel).toBe(result.current.chartData[1]!.dateLabel);
  });
});

// --- useSortedScoreHistory: test all sort branches ---
import { useSortedScoreHistory } from '../../hooks/data/useSortedScoreHistory';
import { PlayerScoreSortMode } from '@festival/core';

function makeScoreHistory(overrides: Partial<ServerScoreHistoryEntry> = {}): ServerScoreHistoryEntry {
  return {
    songId: 'song-1',
    instrument: 'Solo_Guitar',
    newScore: 100000,
    newRank: 5,
    accuracy: 950000,
    isFullCombo: false,
    stars: 5,
    season: 5,
    changedAt: '2025-01-15T10:00:00Z',
    scoreAchievedAt: '2025-01-15T10:00:00Z',
    ...overrides,
  };
}

describe('useSortedScoreHistory â€” all sort branches', () => {
  const a = makeScoreHistory({ newScore: 100000, accuracy: 900000, season: 3, scoreAchievedAt: '2025-01-01T00:00:00Z' });
  const b = makeScoreHistory({ newScore: 200000, accuracy: 950000, season: 5, scoreAchievedAt: '2025-02-01T00:00:00Z' });
  const history = [b, a];

  it('sorts by date ascending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(history, PlayerScoreSortMode.Date, true));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-01-01T00:00:00Z');
  });

  it('sorts by date descending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(history, PlayerScoreSortMode.Date, false));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-02-01T00:00:00Z');
  });

  it('sorts by score ascending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(history, PlayerScoreSortMode.Score, true));
    expect(result.current[0]!.newScore).toBe(100000);
  });

  it('sorts by score descending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(history, PlayerScoreSortMode.Score, false));
    expect(result.current[0]!.newScore).toBe(200000);
  });

  it('sorts by accuracy ascending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(history, PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.accuracy).toBe(900000);
  });

  it('sorts by season ascending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(history, PlayerScoreSortMode.Season, true));
    expect(result.current[0]!.season).toBe(3);
  });

  it('accuracy sort tiebreaker: FC first', () => {
    const x = makeScoreHistory({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const y = makeScoreHistory({ accuracy: 950000, isFullCombo: true, newScore: 100000, scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([x, y], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[1]!.isFullCombo).toBe(true);
  });

  it('accuracy sort tiebreaker: score then date', () => {
    const x = makeScoreHistory({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: '2025-02-01T00:00:00Z' });
    const y = makeScoreHistory({ accuracy: 950000, isFullCombo: false, newScore: 200000, scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([x, y], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.newScore).toBe(100000);
    expect(result.current[1]!.newScore).toBe(200000);
  });

  it('handles null accuracy/season/FC', () => {
    const x = makeScoreHistory({ accuracy: undefined, isFullCombo: undefined, season: undefined });
    const y = makeScoreHistory({ accuracy: 950000, season: 5, isFullCombo: true });
    const { result } = renderHook(() => useSortedScoreHistory([x, y], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.accuracy).toBeUndefined();
  });
});
