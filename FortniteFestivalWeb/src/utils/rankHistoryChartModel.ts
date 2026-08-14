import type { RankHistoryEntry, RankingMetric } from '@festival/core/api';
import { parseSnapshotDate } from './fillRankHistoryGaps';

export type RankHistoryChartPoint = {
  date: string;
  dateLabel: string;
  timestamp: number;
  snapshotTakenAt: string | null;
  isSynthetic: boolean;
  value: number;
  rank: number;
  songsPlayed: number | null;
  coverage: number | null;
  fullComboCount: number | null;
  totalChartedSongs: number | null;
  rankedAccountCount: number | null;
  bayesianValue?: number | null;
};

const YEAR_SUFFIX_LENGTH = -2;

export function toRankHistoryChartPoint(
  entry: RankHistoryEntry,
  metric: RankingMetric,
): RankHistoryChartPoint {
  const date = parseSnapshotDate(entry.snapshotDate);
  return {
    date: entry.snapshotDate,
    dateLabel: formatRankHistoryAxisDate(date),
    timestamp: date.getTime(),
    snapshotTakenAt: entry.snapshotTakenAt ?? null,
    isSynthetic: entry.isSynthetic ?? false,
    value: getValueField(entry, metric),
    rank: getRankField(entry, metric),
    songsPlayed: entry.songsPlayed,
    coverage: entry.coverage,
    fullComboCount: entry.fullComboCount,
    totalChartedSongs: entry.totalChartedSongs,
    rankedAccountCount: entry.rankedAccountCount,
    bayesianValue: getBayesianValueField(entry, metric),
  };
}

export function formatRankHistoryAxisDate(date: Date): string {
  return `${date.getMonth() + 1}/${date.getDate()}/${String(date.getFullYear()).slice(YEAR_SUFFIX_LENGTH)}`;
}

export function formatRankHistoryDisplayDate(snapshotDate: string): string {
  return parseSnapshotDate(snapshotDate).toLocaleDateString('en-US', {
    month: 'short',
    day: 'numeric',
    year: 'numeric',
  });
}

export function isSameRankHistoryPoint(
  left: RankHistoryChartPoint,
  right: RankHistoryChartPoint,
): boolean {
  return left.date === right.date
    && left.rank === right.rank
    && left.value === right.value;
}

export function formatRankHistoryCount(value: number | null): string {
  return value == null ? '—' : value.toLocaleString();
}

export function getRankHistoryTotalSongCount(
  point: RankHistoryChartPoint,
): number | null {
  if (point.totalChartedSongs != null) return point.totalChartedSongs;
  if (point.songsPlayed == null) return null;
  if (point.coverage == null || point.coverage <= 0) return point.songsPlayed;

  const totalSongs = Math.round(point.songsPlayed / point.coverage);
  return Number.isFinite(totalSongs) && totalSongs > 0
    ? totalSongs
    : point.songsPlayed;
}

export function formatRankHistoryFcFraction(
  point: RankHistoryChartPoint,
): string {
  return `${formatRankHistoryCount(point.fullComboCount)} / ${formatRankHistoryCount(getRankHistoryTotalSongCount(point))}`;
}

export function formatRankHistorySongsFraction(
  point: RankHistoryChartPoint,
): string {
  return `${formatRankHistoryCount(point.songsPlayed)} / ${formatRankHistoryCount(getRankHistoryTotalSongCount(point))}`;
}

export function getRecentRankHistoryPoints(
  points: RankHistoryChartPoint[],
  count = 5,
): RankHistoryChartPoint[] {
  return points.length === 0 ? [] : [...points].reverse().slice(0, count);
}

export function getRankHistoryDomain(
  points: RankHistoryChartPoint[],
): [number, number] {
  const ranks = points.map(point => point.rank).filter(rank => rank > 0);
  if (ranks.length === 0) return [1, 100];

  const minRank = Math.min(...ranks);
  const maxRank = Math.max(...ranks);
  const padding = Math.ceil((maxRank - minRank) * 0.1);
  return [
    Math.max(1, minRank - padding),
    maxRank + padding || 100,
  ];
}

export function formatValueTick(
  value: number,
  metric: RankingMetric,
): string {
  switch (metric) {
    case 'fcrate':
    case 'maxscore':
      return `${(value * 100).toFixed(0)}%`;
    case 'totalscore': {
      const absolute = Math.abs(value);
      const sign = value < 0 ? '-' : '';
      if (absolute >= 1_000_000_000) {
        const formatted = absolute / 1_000_000_000;
        return `${sign}${formatted % 1 === 0 ? formatted.toFixed(0) : formatted.toFixed(1)}B`;
      }
      if (absolute >= 1_000_000) {
        const formatted = absolute / 1_000_000;
        return `${sign}${formatted % 1 === 0 ? formatted.toFixed(0) : formatted.toFixed(1)}M`;
      }
      if (absolute >= 1_000) {
        const formatted = absolute / 1_000;
        return `${sign}${formatted % 1 === 0 ? formatted.toFixed(0) : formatted.toFixed(1)}K`;
      }
      return String(Math.round(value));
    }
    case 'adjusted':
    case 'weighted':
      return value.toFixed(2);
  }
}

export function formatDetailValue(
  value: number,
  metric: RankingMetric,
): string {
  switch (metric) {
    case 'fcrate':
    case 'maxscore': {
      const percent = value * 100;
      return percent === 100 || percent % 1 === 0
        ? `${percent.toFixed(0)}%`
        : `${percent.toFixed(1)}%`;
    }
    case 'totalscore':
      return value.toLocaleString();
    case 'adjusted':
    case 'weighted':
      return value % 1 === 0 ? value.toFixed(0) : value.toFixed(1);
  }
}

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
    case 'adjusted': return entry.rawSkillRating ?? entry.adjustedSkillRating ?? 0;
    case 'weighted': return entry.rawWeightedRating ?? entry.weightedRating ?? 0;
    case 'fcrate': return entry.fcRate ?? 0;
    case 'totalscore': return entry.totalScore ?? 0;
    case 'maxscore': return entry.rawMaxScorePercent ?? entry.maxScorePercent ?? 0;
  }
}

function getBayesianValueField(
  entry: RankHistoryEntry,
  metric: RankingMetric,
): number | null {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRating;
    case 'weighted': return entry.weightedRating;
    default: return null;
  }
}
