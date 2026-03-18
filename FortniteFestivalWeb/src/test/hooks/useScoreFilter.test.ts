import { describe, it, expect, beforeEach } from 'vitest';
import { renderHook } from '@testing-library/react';
import React from 'react';
import { useScoreFilter } from '../../hooks/data/useScoreFilter';
import { TestProviders } from '../helpers/TestProviders';

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
