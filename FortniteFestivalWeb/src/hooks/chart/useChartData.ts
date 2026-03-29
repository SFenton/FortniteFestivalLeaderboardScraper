import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { type ServerInstrumentKey as InstrumentKey, type ServerScoreHistoryEntry as ScoreHistoryEntry } from '@festival/core/api/serverTypes';
import { ACCURACY_SCALE } from '@festival/core';

export type ChartPoint = {
  date: string;
  dateLabel: string;
  timestamp: number;
  score: number;
  accuracy: number;
  isFullCombo: boolean;
  colorAccuracy?: number;
  stars?: number;
  season?: number;
  difficulty?: number;
};

/**
 * Fetches score history for a song/account pair via React Query,
 * filters by instrument, and transforms into chart-ready data points.
 */
export function useChartData(
  accountId: string,
  songId: string,
  instrument: InstrumentKey,
  historyProp?: ScoreHistoryEntry[],
) {
  const { data: fetchedHistory, isLoading } = useQuery({
    queryKey: queryKeys.playerHistory(accountId, songId),
    queryFn: () => api.getPlayerHistory(accountId, songId).then(r => r.history),
    enabled: !historyProp && !!accountId && !!songId,
    staleTime: 5 * 60 * 1000,
  });

  const songHistory = useMemo(
    () => historyProp ?? fetchedHistory ?? [],
    [historyProp, fetchedHistory],
  );
  const loading = !historyProp && isLoading;

  const filtered = useMemo(
    () => songHistory.filter(h => h.instrument === instrument),
    [songHistory, instrument],
  );

  const chartData: ChartPoint[] = useMemo(() => {
    const sorted = [...filtered].sort(
      (a, b) => new Date(a.scoreAchievedAt ?? a.changedAt).getTime() - new Date(b.scoreAchievedAt ?? b.changedAt).getTime(),
    );
    const daySeen = new Map<string, number>();
    const dayTotal = new Map<string, number>();
    const YEAR_SUFFIX_LEN = -2;
    const formatDay = (d: Date) => `${d.getMonth() + 1}/${d.getDate()}/${String(d.getFullYear()).slice(YEAR_SUFFIX_LEN)}`;
    for (const h of sorted) {
      const dayKey = formatDay(new Date(h.scoreAchievedAt ?? h.changedAt));
      dayTotal.set(dayKey, (dayTotal.get(dayKey) ?? 0) + 1);
    }
    return sorted.map((h) => {
      const d = new Date(h.scoreAchievedAt ?? h.changedAt);
      const dayKey = formatDay(d);
      const idx = (daySeen.get(dayKey) ?? 0) + 1;
      daySeen.set(dayKey, idx);
      return {
        date: d.toISOString(),
        dateLabel: dayKey,
        timestamp: d.getTime(),
        score: h.newScore,
        accuracy: h.accuracy != null ? h.accuracy / ACCURACY_SCALE : 0,
        isFullCombo: h.isFullCombo ?? false,
        stars: h.stars ?? undefined,
        season: h.season ?? undefined,
        difficulty: h.difficulty ?? undefined,
      };
    });
  }, [filtered]);

  /** Count per-instrument entries for the instrument picker. */
  const instrumentCounts = useMemo(() => {
    const counts: Partial<Record<InstrumentKey, number>> = {};
    for (const h of songHistory) {
      const key = h.instrument as InstrumentKey;
      counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
  }, [songHistory]);

  return { songHistory, chartData, loading, instrumentCounts };
}
