/**
 * Overall summary stat boxes — the first section on the player page.
 * Returns Item[] for: songs played, full combos, gold stars, avg accuracy, best rank.
 */
import { ACCURACY_SCALE } from '@festival/core';
import { SOLO_FAMILY_SCOPE_LABELS, type ServerInstrumentKey as InstrumentKey, type RankingMetric, type SoloFamilyRanksByScope, type SoloFamilyScopeId } from '@festival/core/api/serverTypes';
import { Colors } from '@festival/theme';
import { formatClamped, accuracyColor } from '../helpers/playerStats';
import StatBox from '../../../components/player/StatBox';
import { defaultSongFilters, type SongSettings } from '../../../utils/songSettings';
import type { PlayerItem, NavigateToSongs, NavigateToSongDetail } from '../helpers/playerPageTypes';
import { DEFAULT_METRICS, EXPERIMENTAL_METRICS } from '../../leaderboards/helpers/rankingHelpers';
import PlayerSectionHeading from './PlayerSectionHeading';

const METRIC_I18N_KEY: Record<RankingMetric, string> = {
  totalscore: 'player.totalScoreRank',
  adjusted: 'player.adjustedRank',
  weighted: 'player.weightedRank',
  fcrate: 'player.fcRateRank',
  maxscore: 'player.maxScoreRank',
};

type GlobalStatisticsFamilyScopeId = Extract<SoloFamilyScopeId, 'pad' | 'pro_strings' | 'pro_drums'>;

const FAMILY_SCOPE_INSTRUMENTS: Record<GlobalStatisticsFamilyScopeId, readonly InstrumentKey[]> = {
  pad: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'],
  pro_strings: ['Solo_PeripheralGuitar', 'Solo_PeripheralBass'],
  pro_drums: ['Solo_PeripheralCymbals', 'Solo_PeripheralDrums'],
};

const FAMILY_SCOPE_ORDER: GlobalStatisticsFamilyScopeId[] = ['pad', 'pro_strings', 'pro_drums'];

const FAMILY_SCOPE_DESCRIPTIONS: Record<GlobalStatisticsFamilyScopeId, string> = {
  pad: 'The overall rankings for Lead, Bass, Drums, and Tap Vocals. Selected instrument icons indicate instruments enabled in app settings, but these statistics cards apply to all pad instruments combined.',
  pro_strings: 'The overall rankings for Pro Lead and Pro Bass. Selected instrument icons indicate instruments enabled in app settings, but these statistics cards apply to all pro strings instruments combined.',
  pro_drums: 'The overall rankings for Pro Drums and Pro Cymbals. Selected instrument icons indicate instruments enabled in app settings, but these statistics cards apply to all pro drums instruments combined.',
};

export type GlobalStatisticsFamilySection = {
  scopeId: GlobalStatisticsFamilyScopeId;
  activeInstruments: InstrumentKey[];
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

  for (const box of boxes) {
    items.push({ key: `sum-${box.label}`, span: false, heightEstimate: 100, style: cardStyle, node: <StatBox label={box.label} value={box.value} color={box.color} onClick={box.onClick} /> });
  }

  return items;
}

export function resolveVisibleFamilyRankSections(visibleKeys: readonly InstrumentKey[]): GlobalStatisticsFamilySection[] {
  const visible = new Set(visibleKeys);
  return FAMILY_SCOPE_ORDER
    .map(scopeId => ({
      scopeId,
      activeInstruments: FAMILY_SCOPE_INSTRUMENTS[scopeId].filter(instrument => visible.has(instrument)),
    }))
    .filter(section => section.activeInstruments.length > 0);
}

export function resolveVisibleFamilyRankScopes(visibleKeys: readonly InstrumentKey[]): GlobalStatisticsFamilyScopeId[] {
  return resolveVisibleFamilyRankSections(visibleKeys).map(section => section.scopeId);
}

export function buildFamilyGlobalStatisticsItems(
  t: (key: string, opts?: Record<string, unknown>) => string,
  visibleKeys: InstrumentKey[],
  cardStyle: React.CSSProperties,
  familyRanks?: SoloFamilyRanksByScope | null,
  enableExperimentalRanks?: boolean,
  navigateToFamilyLeaderboard?: (scopeId: SoloFamilyScopeId, metric: RankingMetric, rank?: number) => void,
): PlayerItem[] {
  if (!familyRanks) return [];

  const items: PlayerItem[] = [];
  const metrics: RankingMetric[] = [...DEFAULT_METRICS, ...(enableExperimentalRanks ? EXPERIMENTAL_METRICS : [])];

  for (const section of resolveVisibleFamilyRankSections(visibleKeys)) {
    const ranks = familyRanks[section.scopeId];
    if (!ranks) continue;

    const rankMap: Record<RankingMetric, number | null | undefined> = {
      adjusted: ranks.adjusted,
      weighted: ranks.weighted,
      fcrate: ranks.fcRate,
      totalscore: ranks.totalScore,
      maxscore: ranks.maxScore,
    };

    const cards: PlayerItem[] = [];
    for (const metric of metrics) {
      const rank = rankMap[metric];
      if (rank == null || rank <= 0) continue;

      const label = t(METRIC_I18N_KEY[metric]);
      cards.push({
        key: `family-${section.scopeId}-${metric}`,
        span: false,
        heightEstimate: 100,
        style: cardStyle,
        node: <StatBox label={label} value={`#${rank.toLocaleString()}`} onClick={navigateToFamilyLeaderboard ? () => navigateToFamilyLeaderboard(section.scopeId, metric, rank) : undefined} />,
      });
    }

    if (cards.length === 0) continue;

    items.push({
      key: `family-${section.scopeId}-heading`,
      span: true,
      heightEstimate: 120,
      node: <PlayerSectionHeading title={`${SOLO_FAMILY_SCOPE_LABELS[section.scopeId]} ${t('player.globalStatistics')}`} description={FAMILY_SCOPE_DESCRIPTIONS[section.scopeId]} instruments={section.activeInstruments} />,
    });
    items.push(...cards);
  }

  return items;
}

