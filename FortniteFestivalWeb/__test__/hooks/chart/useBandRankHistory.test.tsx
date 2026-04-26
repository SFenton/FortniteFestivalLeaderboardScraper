import { describe, it, expect, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useBandRankHistory } from '../../../src/hooks/chart/useBandRankHistory';

const { getBandRankHistory } = vi.hoisted(() => ({
  getBandRankHistory: vi.fn().mockResolvedValue({
  bandType: 'Band_Duets',
  teamKey: 'p1:p2',
  comboId: null,
  days: 30,
  historyStatus: 'catching_up',
  historyComputedThrough: '2026-04-22',
  historyJobUpdatedAt: '2026-04-23T12:00:00.000Z',
  historyMessage: 'History is catching up.',
  currentRankingsComputedAt: '2026-04-23T11:00:00.000Z',
  history: [{
    snapshotDate: '2026-04-22',
    snapshotTakenAt: '2026-04-22T06:30:00.0000000Z',
    adjustedSkillRank: 1,
    weightedRank: 2,
    fcRateRank: 3,
    totalScoreRank: 4,
    adjustedSkillRating: 0.1,
    weightedRating: 0.2,
    fcRate: 0.3,
    totalScore: 123456,
    songsPlayed: 10,
    coverage: 0.5,
    fullComboCount: 6,
    totalChartedSongs: 20,
    totalRankedTeams: 200,
    rawWeightedRating: 0.21,
    rawSkillRating: 0.11,
  }],
  }),
}));

vi.mock('../../../src/api/client', () => ({
  api: { getBandRankHistory },
}));

function createWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

describe('useBandRankHistory', () => {
  it('returns chart points and freshness metadata from the band history response', async () => {
    const { result } = renderHook(
      () => useBandRankHistory('Band_Duets', 'p1:p2', 'adjusted', 30),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.chartData.length).toBeGreaterThan(0);
    });

    expect(getBandRankHistory).toHaveBeenCalledWith('Band_Duets', 'p1:p2', 30, undefined);
    expect(result.current.historyStatus).toBe('catching_up');
    expect(result.current.historyComputedThrough).toBe('2026-04-22');
    expect(result.current.historyJobUpdatedAt).toBe('2026-04-23T12:00:00.000Z');
    expect(result.current.historyMessage).toBe('History is catching up.');
    expect(result.current.currentRankingsComputedAt).toBe('2026-04-23T11:00:00.000Z');
    expect(result.current.chartData[0]!.rank).toBe(1);
    expect(result.current.chartData[0]!.value).toBe(0.11);
    expect(result.current.chartData[0]!.rankedAccountCount).toBe(200);
  });
});
