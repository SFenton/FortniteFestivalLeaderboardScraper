/* eslint-disable react/forbid-dom-props -- dynamic styles require inline style prop */
/**
 * Song row components for the songs list, extracted from SongsPage.
 */
import { memo, useMemo, useRef, useCallback, Fragment, useState, useLayoutEffect, type CSSProperties } from 'react';
import InvalidScoreIcon from './InvalidScoreIcon';
import { Link, useLocation } from 'react-router-dom';
import { IoBagHandle, IoChevronForward } from 'react-icons/io5';
import { formatPercentileBucket } from '@festival/core';
import type { ServerSong as Song, PlayerScore, SongDifficulty, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { Colors, Gap, Radius, Font, Weight, InstrumentSize, frostedCard, flexRow, flexColumn, flexCenter, truncate, CssValue, Align, Justify, Display, Position, Layout, BorderStyle, padding } from '@festival/theme';
import { InstrumentIcon, getInstrumentStatusVisual } from '../../../components/display/InstrumentIcons';
import AccuracyDisplay from '../../../components/songs/metadata/AccuracyDisplay';
import PercentilePill from '../../../components/songs/metadata/PercentilePill';
import SongInfo from '../../../components/songs/metadata/SongInfo';
import SeasonPill from '../../../components/songs/metadata/SeasonPill';
import MiniStars from '../../../components/songs/metadata/MiniStars';
import DifficultyBars from '../../../components/songs/metadata/DifficultyBars';
import ScorePill from '../../../components/songs/metadata/ScorePill';
import type { SongSortMode } from '../../../utils/songSettings';
import anim from '../../../styles/animations.module.css';

const INSTRUMENT_DIFFICULTY_KEY: Record<string, keyof SongDifficulty> = {
  Solo_Guitar: 'guitar',
  Solo_Bass: 'bass',
  Solo_Drums: 'drums',
  Solo_Vocals: 'vocals',
  Solo_PeripheralGuitar: 'proGuitar',
  Solo_PeripheralBass: 'proBass',
};

/** Below this container width (px), mobile rows use unified wrapping instead of top-row primary element. */
const MOBILE_PILL_THRESHOLD = 310;

/** Render a single metadata element for the given key. */
function renderMetadataElement(
  key: string,
  score: PlayerScore,
  _allKeys: string[],
  songIntensityRaw?: number,
  maxScore?: number,
  sortMode?: string,
): React.ReactNode | null {
  const stars = score.stars ?? 0;

  switch (key) {
    case 'score':
      if (score.score <= 0) return null;
      if (sortMode === 'maxdistance') {
        return (
          <span style={{ display: 'inline-flex', alignItems: 'baseline', gap: Gap.md }}>
            <ScorePill score={score.score} width="78px" bold />
            <span style={{ color: Colors.textPrimary, fontSize: Font.lg, fontWeight: Weight.bold, flexShrink: 0 }}>/</span>
            {maxScore ? <ScorePill score={maxScore} width="78px" bold textAlign="left" /> : <span style={{ color: Colors.textMuted, fontSize: Font.lg, fontWeight: Weight.bold, width: '78px', display: 'inline-block', textAlign: 'left' }}>—</span>}
          </span>
        );
      }
      return <ScorePill score={score.score} width="78px" bold />;
    case 'percentage':
      return (score.accuracy ?? 0) > 0 ? (
        <AccuracyDisplay
          accuracy={score.accuracy}
          isFullCombo={!!score.isFullCombo}
        />
      ) : null;
    case 'stars':
      return stars > 0 ? (
        <MiniStars starsCount={stars} isFullCombo={!!score.isFullCombo} />
      ) : null;
    case 'seasonachieved':
      return score.season != null && score.season > 0 ? (
        <SeasonPill season={score.season} />
      ) : null;
    case 'percentile': {
      const p = score.rank > 0 && (score.totalEntries ?? 0) > 0
        ? Math.min((score.rank / score.totalEntries!) * 100, 100) : undefined;
      if (p == null) return null;
      return (
        <PercentilePill
          display={formatPercentileBucket(p)}
        />
      );
    }
    case 'intensity':
      return songIntensityRaw != null ? <DifficultyBars level={songIntensityRaw} raw /> : null;
    case 'maxdistance': {
      if (score.score <= 0) return null;
      if (!maxScore) return <PercentilePill display="—" />;
      const pct = (score.score / maxScore) * 100;
      return <PercentilePill display={`${pct.toFixed(1)}%`} />;
    }
    default:
      return null;
  }
}

type NeighborStatus = 'alone' | 'left' | 'right' | 'both';

type MetadataEntry = { key: string; el: React.ReactNode };

/* v8 ignore start — internal presentation component */
function MetadataBottomRow({ entries }: { entries: MetadataEntry[] }) {
  const s = useStyles();
  const [neighborMap, setNeighborMap] = useState<Record<string, NeighborStatus>>({});
  const ref = useRef<HTMLDivElement>(null);

  useLayoutEffect(() => {
    if (!ref.current) return;

    const measureNeighbors = () => {
      const children = ref.current?.querySelectorAll('[data-metadata-key]');
      if (!children || children.length === 0) return;

      // Get top position for each child, rounded to nearest integer for float tolerance
      const positions: Array<{ key: string; top: number }> = [];
      children.forEach((child) => {
        const key = child.getAttribute('data-metadata-key');
        if (key) {
          const rect = child.getBoundingClientRect();
          positions.push({ key, top: Math.round(rect.top) });
        }
      });

      // Group children by their top position
      const grouped = new Map<number, string[]>();
      positions.forEach(({ key, top }) => {
        if (!grouped.has(top)) grouped.set(top, []);
        grouped.get(top)!.push(key);
      });

      // Assign neighbor status to each entry
      const newNeighborMap: Record<string, NeighborStatus> = {};
      grouped.forEach((keys) => {
        if (keys.length === 1) {
          newNeighborMap[keys[0]] = 'alone';
        } else {
          keys.forEach((key, idx) => {
            if (idx === 0) {
              newNeighborMap[key] = 'right';
            } else if (idx === keys.length - 1) {
              newNeighborMap[key] = 'left';
            } else {
              newNeighborMap[key] = 'both';
            }
          });
        }
      });

      setNeighborMap(newNeighborMap);
    };

    // Run measurement after render
    measureNeighbors();

    // Set up ResizeObserver to re-measure on container resize
    const observer = new ResizeObserver(() => {
      measureNeighbors();
    });
    observer.observe(ref.current);

    return () => {
      observer.disconnect();
    };
  }, [entries]);

  if (entries.length === 0) return null;

  return (
    <div ref={ref} style={s.metadataWrap}>
      {entries.map(e => {
        const status = neighborMap[e.key] || 'loading';
        return (
          <div
            key={e.key}
            data-metadata-key={e.key}
            className={`metadataItem metadataItem--${status}`}
            style={s[`metadataItem${status === 'alone' ? 'Alone' : status === 'left' ? 'Left' : status === 'right' ? 'Right' : 'Both'}` as keyof ReturnType<typeof useStyles>]}
          >
            {e.el}
          </div>
        );
      })}
    </div>
  );
}
/* v8 ignore stop */

export { compareByMode } from '../../../utils/songSort';

export const SongRow = memo(function SongRow({ song,
  score,
  instrument,
  instrumentFilter,
  allScoreMap,
  showInstrumentIcons,
  enabledInstruments,
  metadataOrder,
  sortMode,
  isMobile,
  staggerDelay,
  shopHighlight,
  shopHighlightRed,
  externalHref,
  invalidInstruments,
  containerWidth,
}: {
  song: Song;
  score?: PlayerScore;
  instrument: InstrumentKey;
  instrumentFilter?: InstrumentKey | null;
  allScoreMap?: Map<string, PlayerScore>;
  showInstrumentIcons: boolean;
  enabledInstruments: InstrumentKey[];
  metadataOrder: string[];
  sortMode: SongSortMode;
  isMobile: boolean;
  staggerDelay?: number;
  shopHighlight?: boolean;
  /** When true, uses red "leaving tomorrow" pulse instead of blue shop pulse. */
  shopHighlightRed?: boolean;
  /** When set, the row links to this external URL in a new tab instead of routing internally. */
  externalHref?: string;
  /** Map of instrument → hasFallback for invalid scores on this song. */
  invalidInstruments?: Map<InstrumentKey, 'fallback' | 'no-fallback' | 'over-threshold'>;
  /** Container width for threshold-based layout decisions. */
  containerWidth?: number;
}) {
  const s = useStyles();
  const instrumentChips = useMemo(() => {
    if (!showInstrumentIcons || instrumentFilter != null) return null;
    return enabledInstruments.map(key => {
      const ps = allScoreMap?.get(key);
      const hasScore = !!ps && ps.score > 0;
      const isFC = !!ps?.isFullCombo;
      const { fill, stroke } = getInstrumentStatusVisual(hasScore, isFC);
      return { key, fill, stroke };
    });
  }, [showInstrumentIcons, instrumentFilter, allScoreMap, enabledInstruments]);

  const linkRef = useRef<HTMLAnchorElement>(null);
  const location = useLocation();

  /* v8 ignore start — animation cleanup */
  const handleAnimEnd = useCallback(() => {
    const el = linkRef.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);
  /* v8 ignore stop */

  const animStyle: CSSProperties | undefined = staggerDelay != null
    ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${staggerDelay}ms forwards` }
    : undefined;

  const displayOrder = useMemo(() => {
    const order = [...metadataOrder];
    const generalModes = ['title', 'artist', 'year', 'hasfc'];
    if (!generalModes.includes(sortMode)) {
      if (order.includes(sortMode)) {
        return [sortMode, ...order.filter(k => k !== sortMode)];
      }
      // Sort mode not in saved order (e.g. new metadata key) — prepend it
      return [sortMode, ...order];
    }
    return order;
  }, [metadataOrder, sortMode]);

  const diffKey = INSTRUMENT_DIFFICULTY_KEY[instrument];
  const songIntensityRaw = diffKey != null ? song.difficulty?.[diffKey] : undefined;
  const maxScore = song.maxScores?.[instrument];

  const entries = useMemo(() => {
    if (!score || instrumentChips) return [];
    const result: MetadataEntry[] = [];
    for (const key of displayOrder) {
      const el = renderMetadataElement(key, score, displayOrder, songIntensityRaw, maxScore, sortMode);
      if (el) result.push({ key, el });
    }
    return result;
  }, [score, displayOrder, songIntensityRaw, maxScore, sortMode, instrumentChips]);

  /* v8 ignore start -- computed rendering variables with ternaries */
  const rowStyle = isMobile ? s.rowMobile : s.row;
  const rowClassName = shopHighlightRed ? anim.shopHighlightRed : shopHighlight ? anim.shopHighlight : undefined;

  const infoMinWidth = isMobile ? 160 : 200;
  const songInfo = <SongInfo albumArt={song.albumArt} title={song.title} artist={song.artist} year={song.year} minWidth={infoMinWidth} />;

  // External link: render <a> instead of <Link>
  const defaultTo = `/songs/${song.songId}${instrumentFilter != null ? `?instrument=${encodeURIComponent(instrument)}` : ''}`;
  const linkProps = externalHref
    ? { href: externalHref, target: '_blank' as const, rel: 'noopener noreferrer' }
    : undefined;

  const externalIndicator = externalHref ? (
    <div style={s.externalIndicator}>
      <IoBagHandle size={16} />
      <IoChevronForward size={14} />
    </div>
  ) : null;

  // Show icon only when the relevant instrument(s) are invalid:
  // - Instrument-filtered view: only if the selected instrument is in the invalid map
  // - All-instruments (chips) view: if any instrument is invalid
  const hasInvalid = invalidInstruments != null && invalidInstruments.size > 0
    && (instrumentFilter == null || invalidInstruments.has(instrumentFilter));
  const invalidIcon = hasInvalid ? (
    <InvalidScoreIcon songTitle={song.title} invalidInstruments={invalidInstruments!} instrumentFilter={instrumentFilter} />
  ) : null;

  const chipRow = instrumentChips && instrumentChips.length > 0 ? (
    <div style={s.instrumentStatusRow}>
      {instrumentChips.map(c => (
        <div key={c.key} style={{ ...s.instrumentStatusChip, backgroundColor: c.fill, borderColor: c.stroke }}>
          <InstrumentIcon instrument={c.key} size={24} />
        </div>
      ))}
    </div>
  ) : null;
  /* v8 ignore stop */

  if (isMobile && entries.length > 0) {
    const mergedStyle = animStyle ? { ...rowStyle, ...animStyle } : rowStyle;
    const pillFitsTopRow = !containerWidth || containerWidth >= MOBILE_PILL_THRESHOLD;

    if (pillFitsTopRow) {
      // Primary/secondary split: primary entry in detailStrip at top right, rest in bottom row
      const primaryEntry = entries[0];
      const bottomEntries = entries.slice(1);

      return (
        externalHref ? (
          <a ref={linkRef as React.Ref<HTMLAnchorElement>} {...linkProps} className={rowClassName} style={mergedStyle} onAnimationEnd={handleAnimEnd}>
            <div style={s.mobileTopRow}>
              {songInfo}
              <div style={s.detailStrip}>{primaryEntry.el}{invalidIcon}</div>
            </div>
            {bottomEntries.length > 0 && <MetadataBottomRow entries={bottomEntries} />}
            {externalIndicator}
          </a>
        ) : (
          <Link ref={linkRef} to={defaultTo} state={{ backTo: location.pathname }} className={rowClassName} style={mergedStyle} onAnimationEnd={handleAnimEnd}>
            <div style={s.mobileTopRow}>
              {songInfo}
              <div style={s.detailStrip}>{primaryEntry.el}{invalidIcon}</div>
            </div>
            {bottomEntries.length > 0 && <MetadataBottomRow entries={bottomEntries} />}
          </Link>
        )
      );
    }

    // Unified wrapping: all entries in MetadataBottomRow
    return (
      externalHref ? (
        <a ref={linkRef as React.Ref<HTMLAnchorElement>} {...linkProps} className={rowClassName} style={mergedStyle} onAnimationEnd={handleAnimEnd}>
          <div style={s.mobileTopRow}>
            {songInfo}
            {invalidIcon}
          </div>
          <MetadataBottomRow entries={entries} />
          {externalIndicator}
        </a>
      ) : (
        <Link ref={linkRef} to={defaultTo} state={{ backTo: location.pathname }} className={rowClassName} style={mergedStyle} onAnimationEnd={handleAnimEnd}>
          <div style={s.mobileTopRow}>
            {songInfo}
            {invalidIcon}
          </div>
          <MetadataBottomRow entries={entries} />
        </Link>
      )
    );
  }

  if (isMobile && chipRow) {
    const mergedChipStyle = animStyle ? { ...rowStyle, ...animStyle } : rowStyle;
    return (
      externalHref ? (
        /* v8 ignore start -- external href mobile render */
        <a ref={linkRef as React.Ref<HTMLAnchorElement>} {...linkProps} className={rowClassName} style={mergedChipStyle} onAnimationEnd={handleAnimEnd}>
          <div style={s.mobileTopRow}>
            {songInfo}
          </div>
          <div style={{ ...s.instrumentStatusRow, justifyContent: Justify.center }}>
            {instrumentChips!.map(c => (
              <div key={c.key} style={{ ...s.instrumentStatusChip, backgroundColor: c.fill, borderColor: c.stroke }}>
                <InstrumentIcon instrument={c.key} size={24} />
              </div>
            ))}
          </div>
          {externalIndicator}
        </a>
      ) : (
      <Link ref={linkRef} to={`/songs/${song.songId}`} state={{ backTo: location.pathname }} className={rowClassName} style={mergedChipStyle} onAnimationEnd={handleAnimEnd}>
        <div style={s.mobileTopRow}>
          {songInfo}
        </div>
        <div style={s.mobileChipRowWrapper}>
          <div style={{ ...s.instrumentStatusRow, justifyContent: Justify.center }}>
            {instrumentChips!.map(c => (
              <div key={c.key} style={{ ...s.instrumentStatusChip, backgroundColor: c.fill, borderColor: c.stroke }}>
                <InstrumentIcon instrument={c.key} size={24} />
              </div>
            ))}
          </div>
          {invalidIcon && <div style={s.mobileChipInvalidIcon}>{invalidIcon}</div>}
        </div>
      </Link>
      )
    );
  }

  if (isMobile) {
    const mergedPlainStyle = animStyle ? { ...rowStyle, ...animStyle } : rowStyle;
    return (
      externalHref ? (
        <a ref={linkRef as React.Ref<HTMLAnchorElement>} {...linkProps} className={rowClassName} style={mergedPlainStyle} onAnimationEnd={handleAnimEnd}>
          <div style={s.mobileTopRow}>
            {songInfo}
          </div>
          {externalIndicator}
        </a>
      ) : (
      <Link ref={linkRef} to={defaultTo} state={{ backTo: location.pathname }} className={rowClassName} style={mergedPlainStyle} onAnimationEnd={handleAnimEnd}>
        <div style={s.mobileTopRow}>
          {songInfo}
        </div>
      </Link>
      )
    );
  }

  const desktopStyle = animStyle ? { ...rowStyle, ...animStyle } : rowStyle;
  return (
    externalHref ? (
      /* v8 ignore start -- external href desktop render */
      <a ref={linkRef as React.Ref<HTMLAnchorElement>} {...linkProps} className={rowClassName} style={desktopStyle} onAnimationEnd={handleAnimEnd}>
        {songInfo}
        {chipRow}
        {entries.length > 0 && (
          <div style={s.scoreMeta}>
            {entries.map(e => <Fragment key={e.key}>{e.el}</Fragment>)}
          </div>
        )}
        {externalIndicator}
      </a>
      /* v8 ignore stop */
    ) : (
    <Link ref={linkRef} to={defaultTo} state={{ backTo: location.pathname }} className={rowClassName} style={desktopStyle} onAnimationEnd={handleAnimEnd}>
      {songInfo}
      {chipRow}
      {chipRow && invalidIcon}
      {entries.length > 0 && (
        <div style={s.scoreMeta}>
          {entries.map(e => <Fragment key={e.key}>{e.el}</Fragment>)}
          {invalidIcon}
        </div>
      )}
      {!chipRow && !entries.length && invalidIcon}
    </Link>
    )
  );
});

function useStyles() {
  return useMemo(() => ({
    row: { ...frostedCard, ...flexRow, gap: Gap.xl, padding: padding(0, Gap.xl), height: Layout.playerSongRowHeight, borderRadius: Radius.md, overflow: 'hidden', textDecoration: CssValue.none, color: CssValue.inherit } as CSSProperties,
    rowMobile: { ...frostedCard, ...flexColumn, gap: Gap.md, padding: padding(Gap.lg, Gap.xl), borderRadius: Radius.md, overflow: 'hidden', textDecoration: CssValue.none, color: CssValue.inherit } as CSSProperties,
    mobileTopRow: { ...flexRow, gap: Gap.lg, minWidth: 0 } as CSSProperties,
    detailStrip: { ...flexRow, gap: Gap.xl, flexShrink: 0, marginLeft: CssValue.auto } as CSSProperties,
    metadataWrap: { display: Display.flex, flexWrap: 'wrap', alignItems: Align.center, justifyContent: Justify.end, gap: `${Gap.md}px ${Gap.lg}px`, width: CssValue.full } as CSSProperties,
    instrumentStatusRow: { display: Display.flex, gap: Gap.sm, alignItems: Align.center, flexShrink: 0 } as CSSProperties,
    instrumentStatusChip: { width: InstrumentSize.chip, height: InstrumentSize.chip, borderRadius: CssValue.circle, borderWidth: Gap.xs, borderStyle: BorderStyle.solid, ...flexCenter } as CSSProperties,
    rowText: { ...flexColumn, gap: Gap.xs, minWidth: 0, flex: 1 } as CSSProperties,
    rowTitle: { fontSize: Font.md, fontWeight: Weight.semibold, ...truncate } as CSSProperties,
    rowArtist: { fontSize: Font.sm, color: Colors.textSubtle, ...truncate } as CSSProperties,
    scoreMeta: { ...flexRow, gap: Gap.xl, flexShrink: 1, minWidth: 0, overflow: 'hidden' } as CSSProperties,
    mobileChipRowWrapper: { position: Position.relative, display: Display.flex, alignItems: Align.center, justifyContent: Justify.center } as CSSProperties,
    mobileChipInvalidIcon: { position: Position.absolute, right: 0, top: '50%', transform: 'translateY(-50%)' } as CSSProperties,
    externalIndicator: { ...flexRow, gap: Gap.xs, flexShrink: 0, marginLeft: CssValue.auto, color: Colors.textSubtle } as CSSProperties,
    // Metadata item wrapper styles (base and neighbor status variants)
    metadataItemAlone: { width: 'fit-content', marginLeft: CssValue.auto } as CSSProperties,
    metadataItemLeft: {} as CSSProperties,
    metadataItemRight: {} as CSSProperties,
    metadataItemBoth: { padding: `0 ${Gap.md}px` } as CSSProperties,
  }), []);
}
