import type { BandRankingEntry, BandRankingMetric, BandTeamMember, RankingMetric } from '@festival/core/api/serverTypes';
import { coerceRankingMetric, DEFAULT_METRICS } from './rankingHelpers';

export const BAND_EXPERIMENTAL_METRICS: BandRankingMetric[] = ['adjusted', 'weighted', 'fcrate'];
export const BAND_RANKING_METRICS: BandRankingMetric[] = [...DEFAULT_METRICS, ...BAND_EXPERIMENTAL_METRICS] as BandRankingMetric[];

export function getEnabledBandRankingMetrics(experimentalRanksEnabled: boolean): BandRankingMetric[] {
  return experimentalRanksEnabled ? BAND_RANKING_METRICS : ['totalscore'];
}

export function coerceBandRankingMetric(metric: string | RankingMetric | BandRankingMetric | null | undefined, experimentalRanksEnabled: boolean): BandRankingMetric {
  const rankingMetric = coerceRankingMetric(metric as RankingMetric | null | undefined, experimentalRanksEnabled);
  return rankingMetric === 'maxscore' ? 'totalscore' : rankingMetric;
}

export function getBandRankForMetric(entry: BandRankingEntry, metric: BandRankingMetric): number {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRank;
    case 'weighted': return entry.weightedRank;
    case 'fcrate': return entry.fcRateRank;
    case 'totalscore': return entry.totalScoreRank;
  }
}

export function getBandRatingForMetric(entry: BandRankingEntry, metric: BandRankingMetric): number {
  switch (metric) {
    case 'adjusted': return entry.rawSkillRating;
    case 'weighted': return entry.rawWeightedRating ?? entry.weightedRating;
    case 'fcrate': return entry.fcRate;
    case 'totalscore': return entry.totalScore;
  }
}

export function getBandBayesianRatingForMetric(entry: BandRankingEntry, metric: BandRankingMetric): number | undefined {
  switch (metric) {
    case 'adjusted': return entry.adjustedSkillRating;
    case 'weighted': return entry.weightedRating;
    default: return undefined;
  }
}

export function getBandSongsLabel(entry: Pick<BandRankingEntry, 'fullComboCount' | 'songsPlayed' | 'totalChartedSongs'>, metric: BandRankingMetric): string {
  if (metric === 'fcrate') return `${entry.fullComboCount} / ${entry.totalChartedSongs}`;
  return `${entry.songsPlayed} / ${entry.totalChartedSongs}`;
}

export function formatBandTeamName(members: readonly BandTeamMember[], fallbackName: string): string {
  const names = members.map(member => member.displayName?.trim() || fallbackName);
  return names.length > 0 ? names.join(' + ') : fallbackName;
}