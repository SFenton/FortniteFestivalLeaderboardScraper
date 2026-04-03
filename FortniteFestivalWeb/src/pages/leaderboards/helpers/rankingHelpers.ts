import type { RankingMetric, AccountRankingEntry } from '@festival/core/api/serverTypes';
import { Layout } from '@festival/theme';

/** Get the rank value for a given metric from an AccountRankingEntry. */
export function getRankForMetric(entry: AccountRankingEntry, metric: RankingMetric): number {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRank;
    case 'weighted': return entry.weightedRank;
    case 'fcrate': return entry.fcRateRank;
    case 'totalscore': return entry.totalScoreRank;
    case 'maxscore': return entry.maxScorePercentRank;
  }
}

/** Get the rating value for a given metric from an AccountRankingEntry. */
export function getRatingForMetric(entry: AccountRankingEntry, metric: RankingMetric): number {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRating;
    case 'weighted': return entry.weightedRating;
    case 'fcrate': return entry.fcRate;
    case 'totalscore': return entry.totalScore;
    case 'maxscore': return entry.maxScorePercent;
  }
}

/** Build the songs-column label, varying by metric. */
export function getSongsLabel(
  entry: Pick<AccountRankingEntry, 'fullComboCount' | 'songsPlayed' | 'totalChartedSongs'>,
  metric: RankingMetric,
): string {
  if (metric === 'fcrate') return `${entry.fullComboCount} / ${entry.songsPlayed}`;
  return `${entry.songsPlayed} / ${entry.totalChartedSongs}`;
}

/** Format a rating value for display based on the metric type. */
export function formatRating(value: number, metric: RankingMetric): string {
  switch (metric) {
    case 'adjusted':
    case 'weighted':
      return '';
    case 'fcrate':
    case 'maxscore':
      return `${(value * 100).toFixed(1)}%`;
    case 'totalscore':
      return value.toLocaleString();
  }
}

/** The default (non-experimental) metric. */
export const DEFAULT_METRICS: RankingMetric[] = ['totalscore'];

/** Experimental metrics gated behind the enableExperimentalRanks setting. */
export const EXPERIMENTAL_METRICS: RankingMetric[] = ['adjusted', 'weighted', 'fcrate', 'maxscore'];

/** All available ranking metrics in display order. */
export const RANKING_METRICS: RankingMetric[] = [...DEFAULT_METRICS, ...EXPERIMENTAL_METRICS];

/**
 * Compute a pixel-based width that fits the longest formatted rank in a list.
 * Uses a fixed per-character width estimate (tabular-nums at font-size md)
 * to avoid `ch` unit inconsistencies between normal and bold font weights.
 */
export function computeRankWidth(ranks: number[]): number {
  if (ranks.length === 0) return Layout.rankColumnWidth;
  const maxRank = Math.max(...ranks);
  const longest = `#${maxRank.toLocaleString()}`;
  return Math.ceil(longest.length * Layout.rankCharWidth) + Layout.rankColumnPadding;
}
