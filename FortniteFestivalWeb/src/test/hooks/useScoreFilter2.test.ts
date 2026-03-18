import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useScoreFilter } from '../../hooks/data/useScoreFilter';
import { SettingsProvider } from '../../contexts/SettingsContext';
import { FestivalProvider } from '../../contexts/FestivalContext';

vi.mock('../../api/client', () => ({
  api: {
    getSongs: vi.fn().mockResolvedValue({
      songs: [
        { songId: 's1', title: 'Song 1', artist: 'A', maxScores: { Solo_Guitar: 100000, Solo_Bass: 80000 } },
        { songId: 's2', title: 'Song 2', artist: 'B', maxScores: null },
      ],
      season: 1,
    }),
    getPlayerData: vi.fn().mockResolvedValue(null),
  },
}));

function TestWrapper({ children }: { children: React.ReactNode }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return React.createElement(QueryClientProvider, { client: qc },
    React.createElement(SettingsProvider, null,
      React.createElement(FestivalProvider, null, children)
    )
  );
}

describe('useScoreFilter (enabled)', () => {
  beforeEach(() => {
    localStorage.clear();
    // Enable filtering via settings localStorage
    localStorage.setItem('fst:appSettings', JSON.stringify({
      filterInvalidScores: true,
      filterInvalidScoresLeeway: 5,
    }));
  });

  it('enabled is true when setting is on', () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    expect(result.current.enabled).toBe(true);
    expect(result.current.leewayParam).toBe(5);
  });

  it('isScoreValid returns true for valid score', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    // Song s1 has maxScore 100000 with 5% leeway = 105000
    // Need to wait for songs to load
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    expect(result.current.isScoreValid('s1', 'solo_guitar', 100000)).toBe(true);
  });

  it('isScoreValid returns false for invalid score', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    // 110000 > 105000 threshold
    expect(result.current.isScoreValid('s1', 'solo_guitar', 110000)).toBe(false);
  });

  it('isScoreValid returns true for song without maxScores', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    expect(result.current.isScoreValid('s2', 'solo_guitar', 999999)).toBe(true);
  });

  it('isScoreValid returns true for unknown song', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    expect(result.current.isScoreValid('unknown', 'solo_guitar', 999999)).toBe(true);
  });

  it('isScoreValid returns true for unknown instrument', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    expect(result.current.isScoreValid('s1', 'unknown_inst', 999999)).toBe(true);
  });

  it('filterLeaderboard removes invalid entries and re-ranks', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    const entries = [
      { accountId: 'a1', score: 90000, rank: 1 },
      { accountId: 'a2', score: 200000, rank: 2 }, // invalid
      { accountId: 'a3', score: 95000, rank: 3 },
    ] as any[];
    const filtered = result.current.filterLeaderboard('s1', 'solo_guitar', entries);
    expect(filtered).toHaveLength(2);
    expect(filtered[0]!.rank).toBe(1);
    expect(filtered[1]!.rank).toBe(2);
  });

  it('filterPlayerScores removes invalid scores', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    const scores = [
      { songId: 's1', instrument: 'solo_guitar', score: 90000 },
      { songId: 's1', instrument: 'solo_guitar', score: 200000 }, // invalid
    ] as any[];
    const filtered = result.current.filterPlayerScores(scores);
    expect(filtered).toHaveLength(1);
  });

  it('filterHistory removes invalid entries', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    const history = [
      { newScore: 90000 },
      { newScore: 200000 }, // invalid
    ] as any[];
    const filtered = result.current.filterHistory('s1', 'solo_guitar', history);
    expect(filtered).toHaveLength(1);
  });

  it('filterLeaderboard uses custom startRank', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    const entries = [
      { accountId: 'a1', score: 90000, rank: 10 },
    ] as any[];
    const filtered = result.current.filterLeaderboard('s1', 'solo_guitar', entries, 5);
    expect(filtered[0]!.rank).toBe(5);
  });
});
