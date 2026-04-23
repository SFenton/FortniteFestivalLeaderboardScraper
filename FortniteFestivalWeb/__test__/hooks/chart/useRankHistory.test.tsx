import { describe, it, expect, vi } from 'vitest';
import { renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { useRankHistory } from '../../../src/hooks/chart/useRankHistory';
import { fillRankHistoryGaps, parseSnapshotDate } from '../../../src/utils/fillRankHistoryGaps';

vi.mock('../../../src/api/client', () => ({
  api: {
    getRankHistory: vi.fn().mockResolvedValue({
      instrument: 'Solo_Guitar',
      accountId: 'test-player-1',
      history: [{
        snapshotDate: '2026-04-22',
        snapshotTakenAt: '2026-04-22T06:30:00.0000000Z',
        adjustedSkillRank: 1,
        weightedRank: 2,
        fcRateRank: 3,
        totalScoreRank: 4,
        maxScorePercentRank: 5,
        adjustedSkillRating: 0.1,
        weightedRating: 0.2,
        fcRate: 0.3,
        totalScore: 123456,
        maxScorePercent: 0.4,
        songsPlayed: 10,
        coverage: 0.5,
        fullComboCount: 6,
        rawMaxScorePercent: 0.41,
        rawWeightedRating: 0.21,
        rawSkillRating: 0.11,
      }],
    }),
  },
}));

function createWrapper() {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };
}

function rankHistoryEntry(snapshotDate: string) {
  return {
    snapshotDate,
    snapshotTakenAt: `${snapshotDate}T12:00:00.000Z`,
    adjustedSkillRank: 1,
    weightedRank: 2,
    fcRateRank: 3,
    totalScoreRank: 4,
    maxScorePercentRank: 5,
    adjustedSkillRating: 0.1,
    weightedRating: 0.2,
    fcRate: 0.3,
    totalScore: 123456,
    maxScorePercent: 0.4,
    songsPlayed: 10,
    coverage: 0.5,
    fullComboCount: 6,
    rawMaxScorePercent: 0.41,
    rawWeightedRating: 0.21,
    rawSkillRating: 0.11,
  };
}

describe('parseSnapshotDate', () => {
  it('keeps date-only snapshots on the intended calendar day in western time zones', () => {
    const formatter = new Intl.DateTimeFormat('en-US', {
      timeZone: 'America/New_York',
      year: '2-digit',
      month: 'numeric',
      day: 'numeric',
    });

    expect(formatter.format(new Date('2026-04-22'))).toBe('4/21/26');
    expect(formatter.format(parseSnapshotDate('2026-04-22'))).toBe('4/22/26');
  });
});

describe('fillRankHistoryGaps', () => {
  it('extends the last known value through the local current day', () => {
    const filled = fillRankHistoryGaps(
      [rankHistoryEntry('2026-04-22')],
      new Date(2026, 3, 24, 9, 0, 0, 0),
    );

    expect(filled.map((entry) => entry.snapshotDate)).toEqual([
      '2026-04-22',
      '2026-04-23',
      '2026-04-24',
    ]);
    expect(filled[0]!.isSynthetic).toBe(false);
    expect(filled[1]!.isSynthetic).toBe(true);
    expect(filled[1]!.snapshotTakenAt).toBeNull();
    expect(filled[1]!.totalScore).toBe(filled[0]!.totalScore);
    expect(filled[2]!.weightedRank).toBe(filled[0]!.weightedRank);
  });

  it('leaves empty history empty', () => {
    expect(fillRankHistoryGaps([], new Date(2026, 3, 24, 9, 0, 0, 0))).toEqual([]);
  });

  it('does not fabricate future days when the latest snapshot is already current or later', () => {
    const today = new Date(2026, 3, 24, 9, 0, 0, 0);

    expect(fillRankHistoryGaps([rankHistoryEntry('2026-04-24')], today)).toHaveLength(1);
    expect(fillRankHistoryGaps([rankHistoryEntry('2026-04-25')], today)).toHaveLength(1);
  });
});

describe('useRankHistory', () => {
  it('renders the snapshot label from the calendar date instead of a UTC-shifted day', async () => {
    const { result } = renderHook(
      () => useRankHistory('Solo_Guitar', 'test-player-1', 'totalscore', 30),
      { wrapper: createWrapper() },
    );

    await waitFor(() => {
      expect(result.current.chartData).toHaveLength(1);
    });

    expect(result.current.chartData[0]!.dateLabel).toBe('4/22/26');
    expect(result.current.chartData[0]!.timestamp).toBe(parseSnapshotDate('2026-04-22').getTime());
    expect(result.current.chartData[0]!.snapshotTakenAt).toBe('2026-04-22T06:30:00.0000000Z');
    expect(result.current.chartData[0]!.isSynthetic).toBe(false);
  });
});