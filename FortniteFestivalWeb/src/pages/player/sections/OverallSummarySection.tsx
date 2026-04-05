/**
 * Overall summary stat boxes — the first section on the player page.
 * Returns Item[] for: songs played, full combos, gold stars, avg accuracy, best rank.
 */
import { ACCURACY_SCALE } from '@festival/core';
import { type ServerInstrumentKey as InstrumentKey, type CompositeRanks, type RankingMetric } from '@festival/core/api/serverTypes';
import { Colors } from '@festival/theme';
import { formatClamped, accuracyColor } from '../helpers/playerStats';
import StatBox from '../../../components/player/StatBox';
import { defaultSongFilters, type SongSettings } from '../../../utils/songSettings';
import type { PlayerItem, NavigateToSongs, NavigateToSongDetail } from '../helpers/playerPageTypes';
import { DEFAULT_METRICS, EXPERIMENTAL_METRICS } from '../../leaderboards/helpers/rankingHelpers';

const METRIC_I18N_KEY: Record<RankingMetric, string> = {
  totalscore: 'player.totalScoreRank',
  adjusted: 'player.adjustedRank',
  weighted: 'player.weightedRank',
  fcrate: 'player.fcRateRank',
  maxscore: 'player.maxScoreRank',
};

export interface OverallStats {
  songsPlayed: number;
  fcCount: number;
  fcPercent: string;
  goldStarCount: number;
  avgAccuracy: number;
  bestRank: number;
  bestRankSongId: string | null;
  bestRankInstrument: string | null;
}

/* ── Extracted settings updaters (testable) ── */

export function songsPlayedUpdater(visibleKeys: InstrumentKey[]) {
  return (s: SongSettings): SongSettings => {
    const hasScores: Record<string, boolean> = {};
    for (const k of visibleKeys) hasScores[k] = true;
    return { ...s, instrument: null, sortMode: 'title' as const, sortAscending: true, filters: { ...defaultSongFilters(), hasScores } };
  };
}

export function fullCombosUpdater(visibleKeys: InstrumentKey[]) {
  return (s: SongSettings): SongSettings => {
    const hasFCs: Record<string, boolean> = {};
    for (const k of visibleKeys) hasFCs[k] = true;
    return { ...s, instrument: null, sortMode: 'title' as const, sortAscending: true, filters: { ...defaultSongFilters(), hasFCs } };
  };
}

export function buildOverallSummaryItems(
  t: (key: string, opts?: Record<string, unknown>) => string,
  overallStats: OverallStats,
  totalSongs: number,
  visibleKeys: InstrumentKey[],
  navigateToSongs: NavigateToSongs,
  navigateToSongDetail: NavigateToSongDetail,
  cardStyle: React.CSSProperties,
  compositeRanks?: CompositeRanks | null,
  enableExperimentalRanks?: boolean,
  navigateToLeaderboard?: (instrument: InstrumentKey | null, metric: RankingMetric) => void,
): PlayerItem[] {
  const items: PlayerItem[] = [];

  const overallAccColor = overallStats.avgAccuracy > 0
    ? (overallStats.avgAccuracy / ACCURACY_SCALE >= 100 && overallStats.fcPercent === '100.0'
        ? Colors.gold
        : accuracyColor(overallStats.avgAccuracy / ACCURACY_SCALE))
    : undefined;
  const allPlayed = overallStats.songsPlayed >= totalSongs && totalSongs > 0;
  const fcIs100 = overallStats.fcPercent === '100.0';
  const fcValue = fcIs100
    ? overallStats.fcCount.toLocaleString()
    : `${overallStats.fcCount} (${formatClamped(parseFloat(overallStats.fcPercent))}%)`;

  const boxes: { label: string; value: React.ReactNode; color?: string; onClick?: () => void }[] = [
    { label: t('player.songsPlayed'), value: overallStats.songsPlayed.toLocaleString(), color: allPlayed ? Colors.statusGreen : undefined, onClick: () => {
      navigateToSongs(songsPlayedUpdater(visibleKeys));
    } },
    { label: t('player.fullCombos'), value: fcValue, color: fcIs100 ? Colors.gold : undefined, onClick: () => {
      navigateToSongs(fullCombosUpdater(visibleKeys));
    } },
    { label: t('player.goldStars'), value: overallStats.goldStarCount.toLocaleString(), color: Colors.gold },
    { label: t('player.avgAccuracy'), value: overallStats.avgAccuracy > 0 ? formatClamped(overallStats.avgAccuracy / ACCURACY_SCALE) + '%' : '\u2014', color: overallAccColor },
    { label: t('player.bestSongRank'), value: overallStats.bestRank > 0 ? `#${overallStats.bestRank.toLocaleString()}` : '\u2014', onClick: overallStats.bestRankSongId ? () => {
      navigateToSongDetail(overallStats.bestRankSongId!, overallStats.bestRankInstrument! as InstrumentKey, { autoScroll: true });
    } : undefined },
  ];

  // Composite rank cards (after Best Rank)
  if (compositeRanks) {
    const rankMap: Record<RankingMetric, number | null | undefined> = {
      adjusted: compositeRanks.adjusted,
      weighted: compositeRanks.weighted,
      fcrate: compositeRanks.fcRate,
      totalscore: compositeRanks.totalScore,
      maxscore: compositeRanks.maxScore,
    };
    const metrics: RankingMetric[] = [...DEFAULT_METRICS, ...(enableExperimentalRanks ? EXPERIMENTAL_METRICS : [])];
    for (const metric of metrics) {
      const rank = rankMap[metric];
      if (rank != null && rank > 0) {
        boxes.push({
          label: t(METRIC_I18N_KEY[metric]),
          value: `#${rank.toLocaleString()}`,
          onClick: navigateToLeaderboard ? () => navigateToLeaderboard(null, metric) : undefined,
        });
      }
    }
  }

  for (const box of boxes) {
    items.push({ key: `sum-${box.label}`, span: false, heightEstimate: 100, style: cardStyle, node: <StatBox label={box.label} value={box.value} color={box.color} onClick={box.onClick} /> });
  }

  return items;
}

