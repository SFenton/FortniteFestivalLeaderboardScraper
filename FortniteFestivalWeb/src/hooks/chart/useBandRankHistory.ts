import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { BandRankHistoryEntry, BandRankingMetric, BandType, RankHistoryEntry } from '@festival/core/api';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { fillRankHistoryGaps } from '../../utils/fillRankHistoryGaps';
import {
  toRankHistoryChartPoint,
  type RankHistoryChartPoint,
} from '../../utils/rankHistoryChartModel';

function toRankHistoryEntry(entry: BandRankHistoryEntry): RankHistoryEntry {
  return {
    ...entry,
    maxScorePercentRank: 0,
    maxScorePercent: null,
    rawMaxScorePercent: null,
    rankedAccountCount: entry.totalRankedTeams,
  };
}

export function useBandRankHistory(
  bandType: BandType | undefined,
  teamKey: string | undefined,
  metric: BandRankingMetric = 'adjusted',
  days = 30,
  comboId?: string,
) {
  const { data, isLoading, error } = useQuery({
    queryKey: queryKeys.bandRankHistory(bandType ?? '', teamKey ?? '', days, comboId),
    queryFn: ({ signal }) => api.getBandRankHistory(bandType!, teamKey!, days, comboId, { signal }),
    enabled: !!bandType && !!teamKey,
    staleTime: 5 * 60 * 1000,
    refetchInterval: query => query.state.data?.historyStatus === 'catching_up' ? 30 * 1000 : false,
  });

  const chartData: RankHistoryChartPoint[] = useMemo(() => {
    if (!data?.history) return [];
    return fillRankHistoryGaps(data.history.map(toRankHistoryEntry))
      .map(entry => toRankHistoryChartPoint(entry, metric));
  }, [data, metric]);

  return {
    chartData,
    loading: isLoading,
    error,
    hasData: data != null,
    historyStatus: data?.historyStatus ?? 'current',
    historyComputedThrough: data?.historyComputedThrough ?? null,
    historyJobUpdatedAt: data?.historyJobUpdatedAt ?? null,
    historyMessage: data?.historyMessage ?? null,
    currentRankingsComputedAt: data?.currentRankingsComputedAt ?? null,
  };
}
