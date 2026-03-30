import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import React from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useScoreFilter } from '../../../src/hooks/data/useScoreFilter';
import { TestProviders } from '../../helpers/TestProviders';
import { SettingsProvider } from '../../../src/contexts/SettingsContext';
import { FestivalProvider } from '../../../src/contexts/FestivalContext';

vi.mock('../../../src/api/client', () => ({
  api: {
    getSongs: vi.fn().mockResolvedValue({
      songs: [
        { songId: 's1', title: 'Song 1', artist: 'A', maxScores: { Solo_Guitar: 100000, Solo_Bass: 80000 } },
        { songId: 's2', title: 'Song 2', artist: 'B', maxScores: null },
        { songId: 's3', title: 'Song 3', artist: 'C', maxScores: { Solo_Guitar: 0 } },
      ],
      season: 1,
    }),
    getPlayerData: vi.fn().mockResolvedValue(null),
  },
}));

function wrapper({ children }: { children: React.ReactNode }) {
  return React.createElement(TestProviders, null, children);
}

describe('useScoreFilter', () => {
  beforeEach(() => { localStorage.clear(); });

  it('returns isScoreValid = true when filtering disabled (default)', () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper });
    expect(result.current.isScoreValid('s1', 'guitar', 999999)).toBe(true);
    expect(result.current.enabled).toBe(false);
  });

  it('filterLeaderboard returns entries unchanged when disabled', () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper });
    const entries = [{ score: 100, rank: 1 }, { score: 200, rank: 2 }] as any[];
    expect(result.current.filterLeaderboard('s1', 'guitar', entries)).toBe(entries);
  });

  it('filterPlayerScores returns scores unchanged when disabled', () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper });
    const scores = [{ songId: 's1', instrument: 'guitar', score: 100 }] as any[];
    expect(result.current.filterPlayerScores(scores)).toBe(scores);
  });

  it('filterHistory returns history unchanged when disabled', () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper });
    const history = [{ newScore: 100 }] as any[];
    expect(result.current.filterHistory('s1', 'guitar', history)).toBe(history);
  });

  it('leewayParam is undefined when disabled', () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper });
    expect(result.current.leewayParam).toBeUndefined();
  });

  it('isScoreValid returns true when no max score data exists', () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper });
    expect(result.current.isScoreValid('unknown-song', 'guitar', 500000)).toBe(true);
  });
});

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
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    expect(result.current.isScoreValid('s1', 'solo_guitar', 100000)).toBe(true);
  });

  it('isScoreValid returns false for invalid score', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
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
      { accountId: 'a2', score: 200000, rank: 2 },
      { accountId: 'a3', score: 95000, rank: 3 },
    ] as any[];
    const filtered = result.current.filterLeaderboard('s1', 'solo_guitar', entries);
    expect(filtered).toHaveLength(2);
    expect(filtered[0]!.rank).toBe(1);
    expect(filtered[1]!.rank).toBe(2);
  });

  it('filterPlayerScores removes invalid scores without fallback', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    const scores = [
      { songId: 's1', instrument: 'solo_guitar', score: 90000 },
      { songId: 's1', instrument: 'solo_guitar', score: 200000 },
    ] as any[];
    const filtered = result.current.filterPlayerScores(scores);
    expect(filtered).toHaveLength(1);
    expect(filtered[0]!.score).toBe(90000);
  });

  it('filterPlayerScores substitutes fallback values for invalid scores with validScore', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    const scores = [
      { songId: 's1', instrument: 'solo_guitar', score: 90000, rank: 5, accuracy: 9800, isFullCombo: true, stars: 6, totalEntries: 1000 },
      { songId: 's1', instrument: 'solo_guitar', score: 200000, rank: 1, accuracy: 10000, isFullCombo: true, stars: 6, totalEntries: 1000, validScore: 95000, validRank: 3, validAccuracy: 9500, validIsFullCombo: false, validStars: 5, validTotalEntries: 900 },
    ] as any[];
    const filtered = result.current.filterPlayerScores(scores);
    expect(filtered).toHaveLength(2);
    expect(filtered[1]!.score).toBe(95000);
    expect(filtered[1]!.rank).toBe(3);
    expect(filtered[1]!.accuracy).toBe(9500);
    expect(filtered[1]!.isFullCombo).toBe(false);
    expect(filtered[1]!.stars).toBe(5);
    expect(filtered[1]!.totalEntries).toBe(900);
  });

  it('filterPlayerScores uses original values when fallback fields are null', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    const scores = [
      { songId: 's1', instrument: 'solo_guitar', score: 200000, rank: 1, accuracy: 10000, isFullCombo: true, stars: 6, totalEntries: 1000, validScore: 95000, validRank: null, validAccuracy: null, validIsFullCombo: null, validStars: null, validTotalEntries: null },
    ] as any[];
    const filtered = result.current.filterPlayerScores(scores);
    expect(filtered).toHaveLength(1);
    expect(filtered[0]!.score).toBe(95000);
    expect(filtered[0]!.rank).toBe(0);
    expect(filtered[0]!.accuracy).toBe(10000);
    expect(filtered[0]!.isFullCombo).toBe(true);
    expect(filtered[0]!.stars).toBe(6);
    expect(filtered[0]!.totalEntries).toBe(1000);
  });

  it('filterHistory removes invalid entries', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    const history = [
      { newScore: 90000 },
      { newScore: 200000 },
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

  it('isScoreValid returns true for song with zero maxScore (skipped threshold)', async () => {
    const { result } = renderHook(() => useScoreFilter(), { wrapper: TestWrapper });
    await act(async () => { await new Promise(r => setTimeout(r, 50)); });
    expect(result.current.isScoreValid('s3', 'solo_guitar', 999999)).toBe(true);
  });
});
