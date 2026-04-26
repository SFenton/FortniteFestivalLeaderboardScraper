import { useMemo } from 'react';
import { useQuery } from '@tanstack/react-query';
import type { BandRankHistoryEntry, BandRankingMetric, BandType, RankHistoryEntry } from '@festival/core/api/serverTypes';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { fillRankHistoryGaps, parseSnapshotDate } from '../../utils/fillRankHistoryGaps';
import type { RankHistoryChartPoint } from './useRankHistory';

const YEAR_SUFFIX_LEN = -2;
const formatDay = (d: Date) =>
  `${d.getMonth() + 1}/${d.getDate()}/${String(d.getFullYear()).slice(YEAR_SUFFIX_LEN)}`;

function getBandRankField(entry: BandRankHistoryEntry, metric: BandRankingMetric): number {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRank;
    case 'weighted': return entry.weightedRank;
    case 'fcrate': return entry.fcRateRank;
    case 'totalscore': return entry.totalScoreRank;
  }
}

function getBandValueField(entry: BandRankHistoryEntry, metric: BandRankingMetric): number {
  switch (metric) {
    case 'adjusted': return entry.rawSkillRating ?? entry.adjustedSkillRating ?? 0;
    case 'weighted': return entry.rawWeightedRating ?? entry.weightedRating ?? 0;
    case 'fcrate': return entry.fcRate ?? 0;
    case 'totalscore': return entry.totalScore ?? 0;
  }
}

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
    queryFn: () => api.getBandRankHistory(bandType!, teamKey!, days, comboId),
    enabled: !!bandType && !!teamKey,
    staleTime: 5 * 60 * 1000,
  });

  const chartData: RankHistoryChartPoint[] = useMemo(() => {
    if (!data?.history) return [];
    return fillRankHistoryGaps(data.history.map(toRankHistoryEntry)).map((entry) => {
      const bandEntry = entry as RankHistoryEntry & BandRankHistoryEntry;
      const d = parseSnapshotDate(entry.snapshotDate);
      return {
        date: entry.snapshotDate,
        dateLabel: formatDay(d),
        timestamp: d.getTime(),
        snapshotTakenAt: entry.snapshotTakenAt ?? null,
        isSynthetic: entry.isSynthetic ?? false,
        value: getBandValueField(bandEntry, metric),
        rank: getBandRankField(bandEntry, metric),
        songsPlayed: entry.songsPlayed,
        coverage: entry.coverage,
        fullComboCount: entry.fullComboCount,
        totalChartedSongs: entry.totalChartedSongs,
        rankedAccountCount: bandEntry.totalRankedTeams,
        bayesianValue: metric === 'adjusted' ? entry.adjustedSkillRating : metric === 'weighted' ? entry.weightedRating : null,
      };
    });
  }, [data, metric]);

  return { chartData, loading: isLoading, error };
}
