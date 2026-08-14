import { useMemo } from 'react';
import { useQuery, useQueries } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import type { RankingMetric, ServerInstrumentKey as InstrumentKey } from '@festival/core/api';
import { fillRankHistoryGaps } from '../../utils/fillRankHistoryGaps';
import {
  toRankHistoryChartPoint,
  type RankHistoryChartPoint,
} from '../../utils/rankHistoryChartModel';

export { formatDetailValue, formatValueTick } from '../../utils/rankHistoryChartModel';
export type { RankHistoryChartPoint } from '../../utils/rankHistoryChartModel';

export function useRankHistory(
  instrument: InstrumentKey,
  accountId: string | undefined,
  metric: RankingMetric,
  days = 30,
) {
  const { data, isLoading, error } = useQuery({
    queryKey: queryKeys.rankHistory(instrument, accountId ?? '', days),
    queryFn: ({ signal }) => api.getRankHistory(instrument, accountId!, days, { signal }),
    enabled: !!accountId,
    staleTime: 5 * 60 * 1000,
  });

  const chartData: RankHistoryChartPoint[] = useMemo(() => {
    if (!data?.history) return [];
    const filled = fillRankHistoryGaps(data.history);
    return filled.map(entry => toRankHistoryChartPoint(entry, metric));
  }, [data, metric]);

  return { chartData, loading: isLoading, error };
}

const STALE_TIME = 5 * 60 * 1000;

/** Prefetch rank history for all instruments in parallel. Returns per-instrument chartData/loading. */
export function useRankHistoryAll(
  instruments: InstrumentKey[],
  accountId: string | undefined,
  metric: RankingMetric,
  days = 30,
  enabled = true,
) {
  const queries = useQueries({
    queries: instruments.map((inst) => ({
      queryKey: queryKeys.rankHistory(inst, accountId ?? '', days),
      queryFn: ({ signal }: { signal: AbortSignal }) => api.getRankHistory(inst, accountId!, days, { signal }),
      enabled: enabled && !!accountId,
      staleTime: STALE_TIME,
    })),
  });

  return useMemo(() => {
    const result = {} as Record<InstrumentKey, { chartData: RankHistoryChartPoint[]; loading: boolean }>;
    instruments.forEach((inst, i) => {
      const q = queries[i];
      const data = q?.data;
      const chartData: RankHistoryChartPoint[] = data?.history
        ? fillRankHistoryGaps(data.history).map(
            entry => toRankHistoryChartPoint(entry, metric),
          )
        : [];
      result[inst] = { chartData, loading: enabled ? (q?.isLoading ?? true) : false };
    });
    return result;
  }, [queries, instruments, metric, enabled]);
}
