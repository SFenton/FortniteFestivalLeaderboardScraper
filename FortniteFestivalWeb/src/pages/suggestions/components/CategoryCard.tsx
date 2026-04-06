/* eslint-disable react/forbid-dom-props -- useStyles pattern */
/**
 * Suggestion category and song row components extracted from SuggestionsPage.
 */
import { memo, useMemo, type CSSProperties } from 'react';
import { Link } from 'react-router-dom';
import { useTranslation } from 'react-i18next';
import { InstrumentKeys, type InstrumentKey } from '@festival/core/instruments';
import type { LeaderboardData } from '@festival/core/models';
import type { SuggestionCategory, SuggestionSongItem } from '@festival/core/suggestions/types';
import { InstrumentIcon, getInstrumentStatusVisual } from '../../../components/display/InstrumentIcons';
import SongInfo from '../../../components/songs/metadata/SongInfo';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';
import SeasonPill from '../../../components/songs/metadata/SeasonPill';
import { useIsNarrow } from '../../../hooks/ui/useIsMobile';
import {
  Colors, Font, Weight, Gap, Radius, Layout, Border, InstrumentSize, FontVariant,
  Display, Align, Justify, TextAlign, ObjectFit, Overflow, CssValue, CssProp,
  flexRow, flexColumn, flexCenter, frostedCardSurface, border, padding, transition,
} from '@festival/theme';
import { resolveCategoryI18n } from '../suggestionsHelpers';

const BASE = import.meta.env.BASE_URL;

const CORE_TO_SERVER_INSTRUMENT: Record<InstrumentKey, string> = {
  guitar: 'Solo_Guitar',
  bass: 'Solo_Bass',
  drums: 'Solo_Drums',
  vocals: 'Solo_Vocals',
  pro_guitar: 'Solo_PeripheralGuitar',
  pro_bass: 'Solo_PeripheralBass',
};

// ── Category key helpers ──

function getCatInstrument(key: string): InstrumentKey | null {
  const prefixes = ['unfc_', 'unplayed_', 'almost_elite_', 'pct_push_', 'stale_', 'pct_improve_', 'improve_rankings_'];
  let remainder: string | null = null;
  for (const p of prefixes) {
    if (key.startsWith(p)) { remainder = key.substring(p.length); break; }
  }
  if (!remainder) return null;
  const known: InstrumentKey[] = ['pro_guitar', 'pro_bass', 'guitar', 'bass', 'drums', 'vocals'];
  for (const k of known) {
    if (remainder === k || remainder.startsWith(`${k}_`)) return k;
  }
  return null;
}

type RowLayout = 'instrumentChips' | 'singleInstrument' | 'percentile' | 'season' | 'unfcAccuracy' | 'hidden' | 'rival';

function getRowLayout(categoryKey: string): RowLayout {
  const k = categoryKey.toLowerCase();
  // Rival categories use the rival layout
  if (k.startsWith('song_rival_') || k.startsWith('lb_rival_')) return 'rival';
  if (k.startsWith('variety_pack') || k.startsWith('artist_sampler_') || k.startsWith('artist_unplayed_')
    || k.startsWith('unplayed_')
    || (k.startsWith('samename_') && !k.startsWith('samename_nearfc_'))) return 'hidden';
  if (k.startsWith('unfc_')) return 'unfcAccuracy';
  if (k.startsWith('stale_')) return 'season';
  if (k.startsWith('almost_elite') || k.startsWith('pct_push') || k.startsWith('pct_improve') || k.startsWith('same_pct') || k.startsWith('improve_rankings')) return 'percentile';
  if (k.startsWith('near_fc') || k.startsWith('almost_six_star') || k.startsWith('more_stars')
    || k.startsWith('first_plays_mixed') || k.startsWith('star_gains')
    || k.startsWith('samename_nearfc_') || k.startsWith('near_max_')) return 'singleInstrument';
  return 'instrumentChips';
}

// ── CategoryCard ──

export const CategoryCard = memo(function CategoryCard({
  category,
  albumArtMap,
  scoresIndex,
}: {
  category: SuggestionCategory;
  albumArtMap: Map<string, string>;
  scoresIndex: Record<string, LeaderboardData>;
}) {
  const { t } = useTranslation();
  const st = useCategoryStyles();
  const catInstrument = getCatInstrument(category.key);
  const resolved = resolveCategoryI18n(category.key, category.title, category.description);
  const title = resolved ? t(resolved.titleKey, resolved.params) : category.title;
  const description = resolved ? t(resolved.descKey, resolved.params) : category.description;
  return (
    <div style={st.card}>
      <div style={st.cardHeader}>
        <div style={st.cardHeaderRow}>
          <div>
            <span style={st.cardTitle}>{title}</span>
            <span style={st.cardDesc}>{description}</span>
          </div>
          {catInstrument && <InstrumentIcon instrument={catInstrument} size={InstrumentSize.sm} />}
        </div>
      </div>
      <div style={st.songList}>
        {category.songs.map((song) => (
          <SongRow
            key={`${song.songId}-${song.instrumentKey ?? 'any'}`}
            song={ song}
            categoryKey={category.key}
            albumArt={albumArtMap.get(song.songId)}
            leaderboardData={scoresIndex[song.songId]}
          />
        ))}
      </div>
    </div>
  );
});

// ── SongRow ──

/* v8 ignore start — SongRow: presentation component with layout-specific rendering */
export function SongRow({ song, categoryKey, albumArt, leaderboardData,
}: {
  song: SuggestionSongItem;
  categoryKey: string;
  albumArt?: string;
  leaderboardData?: LeaderboardData;
}) {
  const layout = getRowLayout(categoryKey);
  const isNarrow = useIsNarrow();
  const st = useRowStyles();
  const starCount = song.stars ?? 0;
  const isGold = starCount >= 6;
  const displayStars = isGold ? 5 : starCount;
  const showStars = layout === 'singleInstrument' && categoryKey.startsWith('star_gains') && starCount > 0;
  const instrument = song.instrumentKey ?? getCatInstrument(categoryKey);
  const songUrl = instrument
    ? `/songs/${song.songId}?instrument=${CORE_TO_SERVER_INSTRUMENT[instrument]}`
    : `/songs/${song.songId}`;
  const starSrc = isGold ? `${BASE}star_gold.png` : `${BASE}star_white.png`;
  const hasMetadata = layout !== 'hidden';
  const iconOnly = (layout === 'singleInstrument' && !showStars) || layout === 'season';
  const twoRow = isNarrow && hasMetadata && !iconOnly;

  return (
    <Link to={songUrl} style={{ ...st.row, ...(twoRow ? { ...flexColumn, alignItems: Align.stretch } : undefined) }}>
      <div style={twoRow ? st.rowMainLine : { display: Display.contents }}>
        <SongInfo albumArt={albumArt} title={song.title} artist={song.artist} year={song.year} minWidth={0} />
        {!twoRow && <RightContent song={song} layout={layout} leaderboardData={leaderboardData} starCount={showStars ? displayStars : 0} starSrc={starSrc} />}
      </div>
      {twoRow && (
        <div style={st.metadataRow}>
          <RightContent song={song} layout={layout} leaderboardData={leaderboardData} starCount={showStars ? displayStars : 0} starSrc={starSrc} />
        </div>
      )}
    </Link>
  );
}
/* v8 ignore stop */

// ── RightContent ──

function RightContent({ song, layout, leaderboardData, starCount = 0, starSrc,
}: {
  song: SuggestionSongItem;
  layout: RowLayout;
  leaderboardData?: LeaderboardData;
  starCount?: number;
  starSrc?: string;
}) {
  if (layout === 'hidden') return null;

  // Rival layout: shows rival name + rank delta pill + instrument icon
  if (layout === 'rival') {
    const delta = song.rivalRankDelta ?? 0;
    const isAhead = delta > 0;
    const color = isAhead ? Colors.statusGreen : delta < 0 ? Colors.statusRed : Colors.textSecondary;
    const label = delta > 0 ? `+${delta}` : String(delta);
    const isSongRival = song.rivalSource === 'song';
    return (
      <div style={categoryCardStyles.badges}>
        {song.rivalName && (
          <span style={{ ...categoryCardStyles.rivalBadge, backgroundColor: isSongRival ? 'rgba(66,133,244,0.2)' : 'rgba(251,188,4,0.2)', color: isSongRival ? '#4285F4' : '#FBBC04' }}>
            {song.rivalName.length > 12 ? song.rivalName.slice(0, 11) + '…' : song.rivalName}
          </span>
        )}
        {delta !== 0 && <span style={{ ...categoryCardStyles.rivalDelta, color }}>{label}</span>}
        {song.instrumentKey && <InstrumentIcon instrument={song.instrumentKey} size={28} />}
      </div>
    );
  }

  if (layout === 'unfcAccuracy') {
    const pct = song.percent;
    const display = typeof pct === 'number' && pct > 0
      ? `${Math.max(0, Math.min(99, Math.floor(pct)))}%`
      : null;
    if (!display || typeof pct !== 'number') return null;
    const t = Math.min(Math.max(pct / 100, 0), 1);
    const r = Math.round(220 * (1 - t) + 46 * t);
    const g = Math.round(40 * (1 - t) + 204 * t);
    const b = Math.round(40 * (1 - t) + 113 * t);
    return <span style={{ ...categoryCardStyles.unfcPct, color: `rgb(${r},${g},${b})` }}>{display}</span>;
  }

  if (layout === 'season') {
    let season = 0;
    if (leaderboardData) {
      if (song.instrumentKey) {
        const tr = (leaderboardData as Record<string, unknown>)[song.instrumentKey] as { seasonAchieved?: number } | undefined;
        season = tr?.seasonAchieved ?? 0;
      } else {
        for (const ins of InstrumentKeys) {
          const tr = (leaderboardData as Record<string, unknown>)[ins] as { seasonAchieved?: number } | undefined;
          if (tr && (tr.seasonAchieved ?? 0) > season) season = tr.seasonAchieved!;
        }
      }
    }
    return season > 0 ? <SeasonPill season={season} /> : null;
  }

  if (layout === 'percentile') {
    const display = song.percentileDisplay;
    return (
      <div style={categoryCardStyles.badges}>
        {display && (
          <PercentilePill
            display={display}
          />
        )}
        {song.instrumentKey && <InstrumentIcon instrument={song.instrumentKey} size={28} />}
      </div>
    );
  }

  if (layout === 'singleInstrument') {
    return song.instrumentKey ? (
      <div style={categoryCardStyles.badges}>
        {starCount > 0 && starSrc && (
          <span style={categoryCardStyles.starPngInlineRow}>
            {Array.from({ length: starCount }, (_, i) => (
              <img key={i} src={starSrc} alt="★" style={categoryCardStyles.starPngImg} />
            ))}
          </span>
        )}
        <InstrumentIcon instrument={song.instrumentKey} size={28} />
      </div>
    ) : null;
  }

  // instrumentChips
  return (
    <div style={categoryCardStyles.instrumentChipsRow}>
      {InstrumentKeys.map((ins) => {
        const tr = leaderboardData ? (leaderboardData as Record<string, unknown>)[ins] as { numStars?: number; isFullCombo?: boolean } | undefined : undefined;
        const hasScore = !!tr && (tr.numStars ?? 0) > 0;
        const isFC = !!tr?.isFullCombo;
        const { fill, stroke } = getInstrumentStatusVisual(hasScore, isFC);
        return (
          <div key={ins} style={{ ...categoryCardStyles.instrumentChip, backgroundColor: fill, borderColor: stroke }}>
            <InstrumentIcon instrument={ins} size={20} />
          </div>
        );
      })}
    </div>
  );
}

/* ── Styles ── */

function useCategoryStyles() {
  return useMemo(() => ({
    card: {
      ...frostedCardSurface,
      borderRadius: Radius.md,
      marginBottom: Gap.section,
      overflow: Overflow.hidden,
    } as CSSProperties,
    cardHeader: {
      padding: padding(Gap.xl, Gap.section),
      borderBottom: border(Border.thin, Colors.borderSubtle),
    } as CSSProperties,
    cardHeaderRow: {
      ...flexRow,
      justifyContent: Justify.between,
      gap: Gap.xl,
    } as CSSProperties,
    cardTitle: {
      display: Display.block,
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      color: Colors.textPrimary,
      marginBottom: Gap.xs,
    } as CSSProperties,
    cardDesc: {
      display: Display.block,
      fontSize: Font.sm,
      color: Colors.textTertiary,
    } as CSSProperties,
    songList: flexColumn as CSSProperties,
  }), []);
}

function useRowStyles() {
  return useMemo(() => ({
    row: {
      ...flexRow,
      gap: Gap.xl,
      padding: padding(Gap.lg, Gap.section),
      borderBottom: border(Border.thin, Colors.borderSubtle),
      textDecoration: CssValue.none,
      color: CssValue.inherit,
      transition: transition(CssProp.backgroundColor, 120),
      overflow: Overflow.hidden,
      '--frosted-card': '1',
    } as CSSProperties,
    rowMainLine: {
      ...flexRow,
      gap: Gap.xl,
    } as CSSProperties,
    metadataRow: {
      display: Display.flex,
      justifyContent: Justify.end,
      gap: Gap.md,
      paddingTop: Gap.sm,
    } as CSSProperties,
  }), []);
}

const categoryCardStyles = {
  badges: {
    ...flexRow,
    gap: Gap.md,
    flexShrink: 0,
  } as CSSProperties,
  instrumentChipsRow: {
    ...flexRow,
    gap: 6,
    flexShrink: 0,
  } as CSSProperties,
  instrumentChip: {
    width: Layout.instrumentChipSize,
    height: Layout.instrumentChipSize,
    borderRadius: CssValue.circle,
    border: border(Gap.xs, 'currentColor'),
    ...flexCenter,
  } as CSSProperties,
  unfcPct: {
    fontSize: Font.lg,
    fontWeight: Weight.semibold,
    minWidth: Layout.unfcMinWidth,
    textAlign: TextAlign.center,
    flexShrink: 0,
    fontVariantNumeric: FontVariant.tabularNums,
  } as CSSProperties,
  starPngImg: {
    width: Layout.starPngSize,
    height: Layout.starPngSize,
    objectFit: ObjectFit.contain,
  } as CSSProperties,
  starPngInlineRow: {
    display: Display.inlineFlex,
    gap: 3,
    alignItems: Align.center,
  } as CSSProperties,
  rivalBadge: {
    fontSize: Font.xs,
    fontWeight: Weight.semibold,
    borderRadius: Radius.md,
    padding: `${Gap.xs}px ${Gap.sm}px`,
    whiteSpace: 'nowrap',
    overflow: Overflow.hidden,
    textOverflow: 'ellipsis',
    maxWidth: 100,
  } as CSSProperties,
  rivalDelta: {
    fontSize: Font.sm,
    fontWeight: Weight.bold,
    fontVariantNumeric: FontVariant.tabularNums,
    flexShrink: 0,
  } as CSSProperties,
};
