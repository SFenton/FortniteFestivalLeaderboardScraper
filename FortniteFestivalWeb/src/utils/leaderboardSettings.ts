import type { RankingMetric } from '@festival/core/api/serverTypes';
import { RANKING_METRICS } from '../pages/leaderboards/helpers/rankingHelpers';

const STORAGE_KEY = 'fst:leaderboardSettings';
const DEFAULT_METRIC: RankingMetric = 'totalscore';

export function loadLeaderboardRankBy(): RankingMetric {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULT_METRIC;
    const parsed = JSON.parse(raw);
    const metric = parsed?.rankBy;
    if (typeof metric === 'string' && RANKING_METRICS.includes(metric as RankingMetric)) {
      return metric as RankingMetric;
    }
  } catch { /* ignore */ }
  return DEFAULT_METRIC;
}

export function saveLeaderboardRankBy(metric: RankingMetric): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify({ rankBy: metric }));
}
