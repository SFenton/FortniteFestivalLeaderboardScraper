import type { RankingMetric } from '@festival/core/api/serverTypes';

export type NotificationRankingMetricInput = {
  eventKind?: string | null;
  metric?: string | null;
};

const RANK_METRIC_BY_EVENT_KIND: Record<string, RankingMetric> = {
  player_weighted_rank_improved: 'weighted',
  band_weighted_rank_improved: 'weighted',
  player_skill_rank_improved: 'adjusted',
  band_skill_rank_improved: 'adjusted',
  player_total_score_rank_improved: 'totalscore',
  band_total_score_rank_improved: 'totalscore',
  player_fc_rate_rank_improved: 'fcrate',
  band_fc_rate_rank_improved: 'fcrate',
  player_max_score_rank_improved: 'maxscore',
  band_max_score_rank_improved: 'maxscore',
};

const RANK_METRIC_BY_EVENT_METRIC: Record<string, RankingMetric> = {
  weighted_rank: 'weighted',
  skill_rank: 'adjusted',
  adjusted_skill_rank: 'adjusted',
  total_score_rank: 'totalscore',
  fc_rate_rank: 'fcrate',
  max_score_rank: 'maxscore',
  max_score_percent_rank: 'maxscore',
  composite_rank: 'adjusted',
  composite_rank_weighted: 'weighted',
  composite_rank_total_score: 'totalscore',
  composite_rank_fc_rate: 'fcrate',
  composite_rank_max_score: 'maxscore',
};

export function getNotificationRankingMetric(event: NotificationRankingMetricInput): RankingMetric | null {
  const eventKind = event.eventKind?.trim();
  if (eventKind) {
    const byKind = RANK_METRIC_BY_EVENT_KIND[eventKind];
    if (byKind) return byKind;
  }

  const metric = event.metric?.trim();
  if (!metric) return null;
  return RANK_METRIC_BY_EVENT_METRIC[metric] ?? null;
}

export function isAggregateRankNotificationEvent(event: NotificationRankingMetricInput): boolean {
  return getNotificationRankingMetric(event) !== null;
}