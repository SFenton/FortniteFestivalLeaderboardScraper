import type { RankingMetric, AccountRankingEntry, CompositeRankingEntry } from '@festival/core/api/serverTypes';
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
