import type { RankingMetric, AccountRankingEntry, CompositeRankingEntry } from '@festival/core/api/serverTypes';
import { formatPercentileTopExact, formatRatingValue } from '@festival/core';
import { Layout } from '@festival/theme';

export const LEADERBOARD_PAGE_SIZE = 25;

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
    case 'adjusted': return entry.rawSkillRating;
    case 'weighted': return entry.rawWeightedRating ?? entry.weightedRating;
    case 'fcrate': return entry.totalChartedSongs > 0 ? entry.fullComboCount / entry.totalChartedSongs : 0;
    case 'totalscore': return entry.totalScore;
    case 'maxscore': return entry.rawMaxScorePercent ?? entry.maxScorePercent;
  }
}

/** Get the Bayesian-adjusted rating value used for adjusted/weighted ranking order. */
export function getBayesianRatingForMetric(entry: AccountRankingEntry, metric: RankingMetric): number | undefined {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRating;
    case 'weighted': return entry.weightedRating;
    default: return undefined;
  }
}

/** Build the songs-column label, varying by metric. */
export function getSongsLabel(
  entry: Pick<AccountRankingEntry, 'fullComboCount' | 'songsPlayed' | 'totalChartedSongs'>,
  metric: RankingMetric,
): string {
  if (metric === 'fcrate') return `${entry.fullComboCount} / ${entry.totalChartedSongs}`;
  return `${entry.songsPlayed} / ${entry.totalChartedSongs}`;
}

/** Map a 1-based leaderboard rank to the page containing that rank. */
export function getLeaderboardPageForRank(rank: number, pageSize: number = LEADERBOARD_PAGE_SIZE): number {
  if (!Number.isFinite(rank) || rank <= 0) return 1;
  return Math.floor((rank - 1) / pageSize) + 1;
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

export type RankingPillTier = 'top1' | 'top5' | 'default';

/** True when the ranking metric is shown as a user-facing percentile pill. */
export function usesPercentileValueDisplay(metric: RankingMetric): boolean {
  return metric === 'adjusted' || metric === 'weighted';
}

/** Format adjusted/weighted raw percentile values as friendly Top X% labels. */
export function formatRankingValueDisplay(value: number, metric: RankingMetric): string | undefined {
  return usesPercentileValueDisplay(metric) ? formatPercentileTopExact(value) : undefined;
}

/** Format the Bayesian-adjusted ranking value as the raw value pill shown beside percentile metrics. */
export function formatBayesianRatingDisplay(value: number | undefined, metric: RankingMetric): string | undefined {
  return value != null && usesPercentileValueDisplay(metric) ? formatRatingValue(value) : undefined;
}

/** Compute a shared pixel min-width for a set of pill labels. */
export function computePillMinWidth(labels: Array<string | null | undefined>): number | undefined {
  const longest = labels.reduce((max, label) => Math.max(max, label?.length ?? 0), 0);
  if (longest === 0) return undefined;
  return Math.ceil(longest * Layout.rankCharWidth) + Layout.rankColumnPadding;
}

/** Tier percentage metrics the same way rank-history cards do. */
export function getRatingPillTier(value: number, metric: RankingMetric): RankingPillTier | undefined {
  if (metric !== 'fcrate' && metric !== 'maxscore') return undefined;
  const pct = value * 100;
  return pct >= 99 ? 'top1' : pct >= 95 ? 'top5' : 'default';
}

/** The default (non-experimental) metric. */
export const DEFAULT_METRICS: RankingMetric[] = ['totalscore'];

/** Experimental metrics gated behind the experimentalRanks feature flag. */
export const EXPERIMENTAL_METRICS: RankingMetric[] = ['adjusted', 'weighted', 'fcrate', 'maxscore'];

/** All available ranking metrics in display order. */
export const RANKING_METRICS: RankingMetric[] = [...DEFAULT_METRICS, ...EXPERIMENTAL_METRICS];

export function isExperimentalRankingMetric(metric: RankingMetric): boolean {
  return EXPERIMENTAL_METRICS.includes(metric);
}

export function getEnabledRankingMetrics(experimentalRanksEnabled: boolean): RankingMetric[] {
  return experimentalRanksEnabled ? RANKING_METRICS : DEFAULT_METRICS;
}

export function coerceRankingMetric(metric: string | RankingMetric | null | undefined, experimentalRanksEnabled: boolean): RankingMetric {
  if (typeof metric !== 'string' || !RANKING_METRICS.includes(metric as RankingMetric)) {
    return 'totalscore';
  }

  const parsedMetric = metric as RankingMetric;
  return !experimentalRanksEnabled && isExperimentalRankingMetric(parsedMetric)
    ? 'totalscore'
    : parsedMetric;
}

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

/** Get the composite rank value for a given metric from a CompositeRankingEntry. */
export function getCompositeRankForMetric(entry: CompositeRankingEntry, metric: RankingMetric): number | null | undefined {
  switch (metric) {
    case 'adjusted': return entry.compositeRank;
    case 'weighted': return entry.compositeRankWeighted;
    case 'fcrate': return entry.compositeRankFcRate;
    case 'totalscore': return entry.compositeRankTotalScore;
    case 'maxscore': return entry.compositeRankMaxScore;
  }
}
