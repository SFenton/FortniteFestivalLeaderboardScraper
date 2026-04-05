import { useMemo } from 'react';
import { useQuery, useQueries } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import type { RankHistoryEntry, RankingMetric, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';

export type RankHistoryChartPoint = {
  date: string;
  dateLabel: string;
  timestamp: number;
  value: number;
  rank: number;
  songsPlayed: number | null;
  coverage: number | null;
  fullComboCount: number | null;
};

function getRankField(entry: RankHistoryEntry, metric: RankingMetric): number {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRank;
    case 'weighted': return entry.weightedRank;
    case 'fcrate': return entry.fcRateRank;
    case 'totalscore': return entry.totalScoreRank;
    case 'maxscore': return entry.maxScorePercentRank;
  }
}

function getValueField(entry: RankHistoryEntry, metric: RankingMetric): number {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRating ?? 0;
    case 'weighted': return entry.weightedRating ?? 0;
    case 'fcrate': return entry.fcRate ?? 0;
    case 'totalscore': return entry.totalScore ?? 0;
    case 'maxscore': return entry.rawMaxScorePercent ?? entry.maxScorePercent ?? 0;
  }
}

/** Format a value for the bar Y-axis based on the metric type. */
/** Format a value for compact display (axis labels). */
export function formatValueTick(value: number, metric: RankingMetric): string {
  switch (metric) {
    case 'fcrate':
    case 'maxscore':
      return `${(value * 100).toFixed(0)}%`;
    case 'totalscore': {
      const abs = Math.abs(value);
      const sign = value < 0 ? '-' : '';
      if (abs >= 1_000_000_000) {
        const v = abs / 1_000_000_000;
        return `${sign}${v % 1 === 0 ? v.toFixed(0) : v.toFixed(1)}B`;
      }
      if (abs >= 1_000_000) {
        const v = abs / 1_000_000;
        return `${sign}${v % 1 === 0 ? v.toFixed(0) : v.toFixed(1)}M`;
      }
      if (abs >= 1_000) {
        const v = abs / 1_000;
        return `${sign}${v % 1 === 0 ? v.toFixed(0) : v.toFixed(1)}K`;
      }
      return String(Math.round(value));
    }
    case 'adjusted':
    case 'weighted':
      return value.toFixed(2);
  }
}

/** Format a value for detail display (card / list). Shows one decimal for percentages unless whole. */
export function formatDetailValue(value: number, metric: RankingMetric): string {
  switch (metric) {
    case 'fcrate':
    case 'maxscore': {
      const pct = value * 100;
      return pct === 100 || pct % 1 === 0 ? `${pct.toFixed(0)}%` : `${pct.toFixed(1)}%`;
    }
    case 'totalscore':
      return value.toLocaleString();
    case 'adjusted':
    case 'weighted': {
      return value % 1 === 0 ? value.toFixed(0) : value.toFixed(1);
    }
  }
}

const YEAR_SUFFIX_LEN = -2;
const formatDay = (d: Date) =>
  `${d.getMonth() + 1}/${d.getDate()}/${String(d.getFullYear()).slice(YEAR_SUFFIX_LEN)}`;

export function useRankHistory(
  instrument: InstrumentKey,
  accountId: string | undefined,
  metric: RankingMetric,
  days = 30,
) {
  const { data, isLoading, error } = useQuery({
    queryKey: queryKeys.rankHistory(instrument, accountId ?? '', days),
    queryFn: () => api.getRankHistory(instrument, accountId!, days),
    enabled: !!accountId,
    staleTime: 5 * 60 * 1000,
  });

  const chartData: RankHistoryChartPoint[] = useMemo(() => {
    if (!data?.history) return [];
    return data.history.map((entry) => {
      const d = new Date(entry.snapshotDate);
      return {
        date: entry.snapshotDate,
        dateLabel: formatDay(d),
        timestamp: d.getTime(),
        value: getValueField(entry, metric),
        rank: getRankField(entry, metric),
        songsPlayed: entry.songsPlayed,
        coverage: entry.coverage,
        fullComboCount: entry.fullComboCount,
      };
    });
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
) {
  const queries = useQueries({
    queries: instruments.map((inst) => ({
      queryKey: queryKeys.rankHistory(inst, accountId ?? '', days),
      queryFn: () => api.getRankHistory(inst, accountId!, days),
      enabled: !!accountId,
      staleTime: STALE_TIME,
    })),
  });

  return useMemo(() => {
    const result = {} as Record<InstrumentKey, { chartData: RankHistoryChartPoint[]; loading: boolean }>;
    instruments.forEach((inst, i) => {
      const q = queries[i];
      const data = q?.data;
      const chartData: RankHistoryChartPoint[] = data?.history
        ? data.history.map((entry) => {
            const d = new Date(entry.snapshotDate);
            return {
              date: entry.snapshotDate,
              dateLabel: formatDay(d),
              timestamp: d.getTime(),
              value: getValueField(entry, metric),
              rank: getRankField(entry, metric),
              songsPlayed: entry.songsPlayed,
              coverage: entry.coverage,
              fullComboCount: entry.fullComboCount,
            };
          })
        : [];
      result[inst] = { chartData, loading: q?.isLoading ?? true };
    });
    return result;
  }, [queries, instruments, metric]);
}
