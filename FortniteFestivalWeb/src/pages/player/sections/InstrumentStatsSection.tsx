/**
 * Per-instrument statistics section — stat cards + percentile table.
 * Returns Item[] for ONE instrument.
 */
import { ACCURACY_SCALE } from '@festival/core';
import { InstrumentHeaderSize } from '@festival/core';
import { type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import type { PlayerScore } from '@festival/core/api/serverTypes';
import { Colors, Gap } from '@festival/theme';
import { computeInstrumentStats, formatClamped, formatClamped2, accuracyColor } from '../helpers/playerStats';
import StatBox from '../../../components/player/StatBox';
import { cleanFilters, buildStarFilter, buildPercentileFilter } from '../helpers/playerFilterHelpers';
import sHeading from './PlayerSectionHeading.module.css';
import type { PlayerItem, NavigateToSongs, NavigateToSongDetail } from '../helpers/playerPageTypes';
import InstrumentHeader from '../../../components/display/InstrumentHeader';
import { PlayerPercentileHeader, PlayerPercentileRow } from '../../../components/player/PlayerPercentileTable';
import GoldStars from '../../../components/songs/metadata/GoldStars';
import { SongSettings } from '../../../utils/songSettings';

/* ── Extracted settings updaters (testable) ── */

export function instSongsPlayedUpdater(inst: InstrumentKey) {
  return (s: SongSettings): SongSettings => ({ ...s, instrument: inst, sortMode: 'score' as const, sortAscending: true, filters: { ...cleanFilters(s, inst), hasScores: { ...s.filters.hasScores, [inst]: true } } });
}

export function instFCsUpdater(inst: InstrumentKey) {
  return (s: SongSettings): SongSettings => ({ ...s, instrument: inst, sortMode: 'score' as const, sortAscending: true, filters: { ...cleanFilters(s, inst), hasFCs: { ...s.filters.hasFCs, [inst]: true } } });
}

export function instStarsUpdater(inst: InstrumentKey, starKey: number) {
  return (s: SongSettings): SongSettings => ({ ...s, instrument: inst, sortMode: 'stars' as const, sortAscending: true, filters: { ...cleanFilters(s, inst), starsFilter: buildStarFilter(starKey) } });
}

export function instPercentileUpdater(inst: InstrumentKey) {
  return (s: SongSettings): SongSettings => ({ ...s, instrument: inst, sortMode: 'percentile' as const, sortAscending: true, filters: cleanFilters(s, inst) });
}

export function instPercentileWithScoresUpdater(inst: InstrumentKey) {
  return (s: SongSettings): SongSettings => ({ ...s, instrument: inst, sortMode: 'percentile' as const, sortAscending: true, filters: { ...cleanFilters(s, inst), hasScores: { ...s.filters.hasScores, [inst]: true } } });
}

export function instPercentileBucketUpdater(inst: InstrumentKey, pct: number) {
  return (s: SongSettings): SongSettings => ({ ...s, instrument: inst, sortAscending: true, filters: { ...cleanFilters(s, inst), percentileFilter: buildPercentileFilter(pct) } });
}

export function pctGold(v: string): string | undefined {
  return /^Top [1-5]%$/.test(v) ? Colors.gold : undefined;
}

export function buildInstrumentStatsItems(
  t: (key: string, opts?: Record<string, unknown>) => string,
  inst: InstrumentKey,
  scores: PlayerScore[],
  totalSongs: number,
  _displayName: string,
  navigateToSongs: NavigateToSongs,
  navigateToSongDetail: NavigateToSongDetail,
  cardStyle: React.CSSProperties,
): PlayerItem[] {
  if (scores.length === 0) return [];

  const items: PlayerItem[] = [];
  const stats = computeInstrumentStats(scores, totalSongs);

  // Instrument header
  items.push({
    key: `inst-hdr-${inst}`,
    span: true,
    heightEstimate: 64,
    node: (
      <div className={sHeading.instCardHeader}>
        <InstrumentHeader instrument={inst} size={InstrumentHeaderSize.MD} />
      </div>
    ),
  });

  // Build stat cards
  /* v8 ignore start — stat card navigation click handlers */
  const cards: { label: string; value: React.ReactNode; color?: string; onClick?: () => void }[] = [];

  if (stats.songsPlayed > 0) {
    cards.push({ label: t('player.songsPlayed'), value: stats.songsPlayed.toLocaleString(), color: stats.songsPlayed >= totalSongs ? Colors.statusGreen : undefined, onClick: () => {
      navigateToSongs(instSongsPlayedUpdater(inst));
    } });
  }
  if (stats.fcCount > 0) {
    cards.push({ label: t('player.fcs'), value: stats.fcPercent === '100.0' ? stats.fcCount.toLocaleString() : `${stats.fcCount} (${stats.fcPercent}%)`, color: stats.fcPercent === '100.0' ? Colors.gold : undefined, onClick: () => {
      navigateToSongs(instFCsUpdater(inst));
    } });
  }

  // Star count cards
  const STAR_CARDS: { count: number; label: string; starKey: number; color?: string }[] = [
    { count: stats.goldStarCount, label: t('player.goldStars'), starKey: 6, color: Colors.gold },
    { count: stats.fiveStarCount, label: t('player.fiveStars'), starKey: 5 },
    { count: stats.fourStarCount, label: t('player.fourStars'), starKey: 4 },
    { count: stats.threeStarCount, label: t('player.threeStars'), starKey: 3 },
    { count: stats.twoStarCount, label: t('player.twoStars'), starKey: 2 },
    { count: stats.oneStarCount, label: t('player.oneStar'), starKey: 1 },
  ];
  for (const sc of STAR_CARDS) {
    if (sc.count > 0) {
      cards.push({ label: sc.label, value: sc.count.toLocaleString(), color: sc.color, onClick: () => {
        navigateToSongs(instStarsUpdater(inst, sc.starKey));
      } });
    }
  }

  const accPct = stats.avgAccuracy / ACCURACY_SCALE;
  const isGoldAcc = accPct >= 100 && stats.fcPercent === '100.0';
  const accColor = stats.avgAccuracy > 0 ? (isGoldAcc ? Colors.gold : accuracyColor(accPct)) : undefined;
  cards.push({ label: t('player.avgAccuracy'), value: stats.avgAccuracy > 0 ? formatClamped(accPct) + '%' : '\u2014', color: accColor });
  cards.push({ label: t('player.avgStars'), value: stats.averageStars === 6 ? <GoldStars /> : (stats.averageStars > 0 ? formatClamped2(stats.averageStars) : '\u2014') });
  cards.push({ label: t('player.bestRank'), value: stats.bestRank > 0 ? `#${stats.bestRank.toLocaleString()}` : '\u2014', onClick: stats.bestRankSongId ? () => navigateToSongDetail(stats.bestRankSongId!, inst, { autoScroll: true }) : undefined });
  cards.push({ label: t('player.percentile'), value: stats.overallPercentile, color: pctGold(stats.overallPercentile), onClick: () => {
    navigateToSongs(instPercentileUpdater(inst));
  } });
  cards.push({ label: t('player.songsPlayed'), value: stats.avgPercentile, color: pctGold(stats.avgPercentile), onClick: () => {
    navigateToSongs(instPercentileWithScoresUpdater(inst));
  } });
  /* v8 ignore stop */

  for (let ci = 0; ci < cards.length; ci++) {
    const c = cards[ci]!;
    items.push({ key: `${inst}-card-${ci}`, span: false, heightEstimate: 100, style: cardStyle, node: <StatBox label={c.label} value={c.value} color={c.color} onClick={c.onClick} /> });
  }

  // Percentile table
  if (stats.percentileBuckets.length > 0) {
    items.push({
      key: `${inst}-pct-table`,
      span: true,
      heightEstimate: 40 + stats.percentileBuckets.length * 44,
      style: { ...cardStyle, overflow: 'hidden' as const, marginBottom: Gap.md },
      node: (
        <div>
          <PlayerPercentileHeader percentileLabel={t('player.percentileHeader')} songsLabel={t('player.songsHeader')} />
          {stats.percentileBuckets.map((b, pi) => (
            <PlayerPercentileRow
              key={b.pct}
              pct={b.pct}
              count={b.count}
              isLast={pi === stats.percentileBuckets.length - 1}
              onClick={() => {
                navigateToSongs(instPercentileBucketUpdater(inst, b.pct));
              }}
            />
          ))}
        </div>
      ),
    });
  }

  return items;
}
