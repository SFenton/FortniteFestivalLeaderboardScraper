import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useSortedScoreHistory } from '../../hooks/data/useSortedScoreHistory';
import { PlayerScoreSortMode } from '@festival/core';
import type { ServerScoreHistoryEntry } from '@festival/core/api/serverTypes';

function entry(overrides: Partial<ServerScoreHistoryEntry> = {}): ServerScoreHistoryEntry {
  return {
    id: 0,
    songId: 's1',
    instrument: 'guitar',
    oldScore: 0,
    newScore: 1000,
    changedAt: '2025-01-01T00:00:00Z',
    scoreAchievedAt: '2025-01-01T00:00:00Z',
    accuracy: 500000,
    isFullCombo: false,
    stars: 3,
    season: 1,
    ...overrides,
  } as ServerScoreHistoryEntry;
}

describe('useSortedScoreHistory', () => {
  const entries: ServerScoreHistoryEntry[] = [
    entry({ newScore: 500, scoreAchievedAt: '2025-03-01T00:00:00Z', accuracy: 900000, season: 2 }),
    entry({ newScore: 1000, scoreAchievedAt: '2025-01-01T00:00:00Z', accuracy: 800000, season: 1 }),
    entry({ newScore: 750, scoreAchievedAt: '2025-02-01T00:00:00Z', accuracy: 950000, season: 3 }),
  ];

  it('sorts by score ascending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, PlayerScoreSortMode.Score, true));
    expect(result.current.map(e => e.newScore)).toEqual([500, 750, 1000]);
  });

  it('sorts by score descending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, PlayerScoreSortMode.Score, false));
    expect(result.current.map(e => e.newScore)).toEqual([1000, 750, 500]);
  });

  it('sorts by date ascending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, PlayerScoreSortMode.Date, true));
    expect(result.current.map(e => e.newScore)).toEqual([1000, 750, 500]);
  });

  it('sorts by date descending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, PlayerScoreSortMode.Date, false));
    expect(result.current.map(e => e.newScore)).toEqual([500, 750, 1000]);
  });

  it('sorts by accuracy ascending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, PlayerScoreSortMode.Accuracy, true));
    expect(result.current.map(e => e.accuracy)).toEqual([800000, 900000, 950000]);
  });

  it('sorts by season ascending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, PlayerScoreSortMode.Season, true));
    expect(result.current.map(e => e.season)).toEqual([1, 2, 3]);
  });

  it('sorts by season descending', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, PlayerScoreSortMode.Season, false));
    expect(result.current.map(e => e.season)).toEqual([3, 2, 1]);
  });

  it('accuracy sort uses FC as tiebreaker', () => {
    const tied = [
      entry({ accuracy: 900000, isFullCombo: false, newScore: 100 }),
      entry({ accuracy: 900000, isFullCombo: true, newScore: 100 }),
    ];
    const { result } = renderHook(() => useSortedScoreHistory(tied, PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.isFullCombo).toBe(false);
    expect(result.current[1]!.isFullCombo).toBe(true);
  });

  it('accuracy sort uses score as second tiebreaker', () => {
    const tied = [
      entry({ accuracy: 900000, isFullCombo: false, newScore: 200 }),
      entry({ accuracy: 900000, isFullCombo: false, newScore: 100 }),
    ];
    const { result } = renderHook(() => useSortedScoreHistory(tied, PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.newScore).toBe(100);
    expect(result.current[1]!.newScore).toBe(200);
  });

  it('returns empty array for empty input', () => {
    const { result } = renderHook(() => useSortedScoreHistory([], PlayerScoreSortMode.Score, true));
    expect(result.current).toEqual([]);
  });

  it('handles null season values', () => {
    const withNull = [
      entry({ season: null as any, newScore: 1 }),
      entry({ season: 2, newScore: 2 }),
    ];
    const { result } = renderHook(() => useSortedScoreHistory(withNull, PlayerScoreSortMode.Season, true));
    expect(result.current[0]!.newScore).toBe(1); // null coalesces to 0, sorts first
    expect(result.current[1]!.newScore).toBe(2);
  });

  it('date sort falls back to changedAt when scoreAchievedAt is null', () => {
    const entries2 = [
      entry({ scoreAchievedAt: undefined, changedAt: '2025-03-01T00:00:00Z', newScore: 300 }),
      entry({ scoreAchievedAt: undefined, changedAt: '2025-01-01T00:00:00Z', newScore: 100 }),
    ];
    const { result } = renderHook(() => useSortedScoreHistory(entries2, PlayerScoreSortMode.Date, true));
    expect(result.current[0]!.newScore).toBe(100);
    expect(result.current[1]!.newScore).toBe(300);
  });

  it('accuracy sort falls back to date when score and FC match', () => {
    const tied = [
      entry({ accuracy: 900000, isFullCombo: false, newScore: 100, scoreAchievedAt: '2025-03-01T00:00:00Z' }),
      entry({ accuracy: 900000, isFullCombo: false, newScore: 100, scoreAchievedAt: '2025-01-01T00:00:00Z' }),
    ];
    const { result } = renderHook(() => useSortedScoreHistory(tied, PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-01-01T00:00:00Z');
    expect(result.current[1]!.scoreAchievedAt).toBe('2025-03-01T00:00:00Z');
  });

  it('handles unknown sort mode', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, 'unknown' as PlayerScoreSortMode, true));
    // Unknown mode returns 0 comparison, order preserved
    expect(result.current.length).toBe(3);
  });

  it('handles null accuracy in accuracy sort', () => {
    const withNull = [
      entry({ accuracy: null as any, newScore: 200 }),
      entry({ accuracy: 900000, newScore: 100 }),
    ];
    const { result } = renderHook(() => useSortedScoreHistory(withNull, PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.newScore).toBe(200);
  });

  it('accuracy sort uses date as third tiebreaker', () => {
    const tied = [
      entry({ accuracy: 900000, isFullCombo: false, newScore: 100, scoreAchievedAt: '2025-06-01T00:00:00Z' }),
      entry({ accuracy: 900000, isFullCombo: false, newScore: 100, scoreAchievedAt: '2025-01-01T00:00:00Z' }),
    ];
    const { result } = renderHook(() => useSortedScoreHistory(tied, PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-01-01T00:00:00Z');
    expect(result.current[1]!.scoreAchievedAt).toBe('2025-06-01T00:00:00Z');
  });

  it('handles unknown sort mode via default case', () => {
    const { result } = renderHook(() => useSortedScoreHistory(entries, 'unknown' as any, true));
    expect(result.current).toHaveLength(3);
  });
});
