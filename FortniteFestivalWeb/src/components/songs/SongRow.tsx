/**
 * Song row components for the songs list, extracted from SongsPage.
 */
import { memo, useMemo, useRef, useCallback, Fragment, type CSSProperties } from 'react';
import { Link, useLocation } from 'react-router-dom';
import { formatPercentileBucket } from '@festival/core';
import type { Song, PlayerScore, SongDifficulty, InstrumentKey } from '../../models';
import { Size } from '@festival/theme';
import { InstrumentIcon, getInstrumentStatusVisual } from '../InstrumentIcons';
import AlbumArt from '../AlbumArt';
import SeasonPill from '../SeasonPill';
import MiniStars from './MiniStars';
import DifficultyBars from './DifficultyBars';
import type { SongSortMode } from '../songSettings';
import s from '../../pages/SongsPage.module.css';

const INSTRUMENT_DIFFICULTY_KEY: Record<string, keyof SongDifficulty> = {
  Solo_Guitar: 'guitar',
  Solo_Bass: 'bass',
  Solo_Drums: 'drums',
  Solo_Vocals: 'vocals',
  Solo_PeripheralGuitar: 'proGuitar',
  Solo_PeripheralBass: 'proBass',
};

/** Render a single metadata element for the given key. */
function renderMetadataElement(
  key: string,
  score: PlayerScore,
  _allKeys: string[],
  songIntensityRaw?: number,
): React.ReactNode | null {
  const rawAcc = score.accuracy ?? 0;
  const pct = rawAcc > 0 ? rawAcc / 10000 : 0;
  const accuracy = pct > 0
    ? (pct % 1 === 0 ? `${Math.round(pct)}%` : `${pct.toFixed(1)}%`)
    : undefined;
  const stars = score.stars ?? 0;

  switch (key) {
    case 'score':
      return score.score > 0 ? (
        <span className={s.scoreValue}>{score.score.toLocaleString()}</span>
      ) : null;
    case 'percentage':
      return accuracy ? (
        <span className={score.isFullCombo ? s.accuracyBadgeFC : s.accuracyPill}>{accuracy}</span>
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
      const display = formatPercentileBucket(p);
      const isTop1 = p <= 1;
      const isTop5 = p <= 5;
      const pctClass = isTop1 ? s.percentileBadgeTop1 : isTop5 ? s.percentileBadgeTop5 : s.percentilePill;
      return <span className={pctClass}>{display}</span>;
    }
    case 'intensity':
      return songIntensityRaw != null ? <DifficultyBars level={songIntensityRaw} raw /> : null;
    default:
      return null;
  }
}

type MetadataEntry = { key: string; el: React.ReactNode };

function MetadataBottomRow({ entries }: { entries: MetadataEntry[] }) {
  if (entries.length === 0) return null;
  return (
    <div className={s.metadataWrap}>
      {entries.map(e => <Fragment key={e.key}>{e.el}</Fragment>)}
    </div>
  );
}

/** Compare two PlayerScores by a given sort mode; undefined scores sort last. */
export function compareByMode(mode: SongSortMode, a?: PlayerScore, b?: PlayerScore): number {
  if (!a && !b) return 0;
  if (!a) return 1;
  if (!b) return -1;
  switch (mode) {
    case 'score':
      return a.score - b.score;
    case 'percentage': {
      const pa = a.accuracy ?? 0;
      const pb = b.accuracy ?? 0;
      if (pa !== pb) return pa - pb;
      return (a.isFullCombo ? 1 : 0) - (b.isFullCombo ? 1 : 0);
    }
    case 'percentile': {
      const pa = a.rank > 0 && (a.totalEntries ?? 0) > 0 ? a.rank / a.totalEntries! : Infinity;
      const pb = b.rank > 0 && (b.totalEntries ?? 0) > 0 ? b.rank / b.totalEntries! : Infinity;
      return pa - pb;
    }
    case 'stars':
      return (a.stars ?? 0) - (b.stars ?? 0);
    case 'seasonachieved':
      return (a.season ?? 0) - (b.season ?? 0);
    case 'hasfc':
      return (a.isFullCombo ? 1 : 0) - (b.isFullCombo ? 1 : 0);
    default:
      return 0;
  }
}

export const SongRow = memo(function SongRow({
  song,
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
}) {
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

  const handleAnimEnd = useCallback(() => {
    const el = linkRef.current;
    if (!el) return;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);

  const animStyle: CSSProperties | undefined = staggerDelay != null
    ? { opacity: 0, animation: `fadeInUp 400ms ease-out ${staggerDelay}ms forwards` }
    : undefined;

  const displayOrder = useMemo(() => {
    const order = [...metadataOrder];
    const generalModes = ['title', 'artist', 'year', 'hasfc'];
    if (!generalModes.includes(sortMode) && order.includes(sortMode)) {
      return [sortMode, ...order.filter(k => k !== sortMode)];
    }
    return order;
  }, [metadataOrder, sortMode]);

  const diffKey = INSTRUMENT_DIFFICULTY_KEY[instrument];
  const songIntensityRaw = diffKey != null ? song.difficulty?.[diffKey] : undefined;

  const entries = useMemo(() => {
    if (!score || instrumentChips) return [];
    const result: MetadataEntry[] = [];
    for (const key of displayOrder) {
      const el = renderMetadataElement(key, score, displayOrder, songIntensityRaw);
      if (el) result.push({ key, el });
    }
    return result;
  }, [score, displayOrder, songIntensityRaw, instrumentChips]);

  const thumb = <AlbumArt src={song.albumArt} size={Size.thumb} />;

  const chipRow = instrumentChips && instrumentChips.length > 0 ? (
    <div className={s.instrumentStatusRow}>
      {instrumentChips.map(c => (
        <div key={c.key} className={s.instrumentStatusChip} style={{ backgroundColor: c.fill, borderColor: c.stroke }}>
          <InstrumentIcon instrument={c.key} size={24} />
        </div>
      ))}
    </div>
  ) : null;

  if (isMobile && entries.length > 0) {
    const primaryKey = entries[0]?.key;
    const scoreEntry = primaryKey ? entries.find(e => e.key === primaryKey) : null;
    const bottomEntries = primaryKey ? entries.filter(e => e.key !== primaryKey) : entries;
    return (
      <Link ref={linkRef} to={`/songs/${song.songId}${instrumentFilter != null ? `?instrument=${encodeURIComponent(instrument)}` : ''}`} state={{ backTo: location.pathname }} className={s.rowMobile} style={animStyle} onAnimationEnd={handleAnimEnd}>
        <div className={s.mobileTopRow}>
          {thumb}
          <div className={s.rowText}>
            <span className={s.rowTitle}>{song.title}</span>
            <span className={s.rowArtist}>{song.artist}{song.year ? ` · ${song.year}` : ''}</span>
          </div>
          {scoreEntry && <div className={s.detailStrip}>{scoreEntry.el}</div>}
        </div>
        {bottomEntries.length > 0 && <MetadataBottomRow entries={bottomEntries} />}
      </Link>
    );
  }

  if (isMobile && chipRow) {
    return (
      <Link ref={linkRef} to={`/songs/${song.songId}`} state={{ backTo: location.pathname }} className={s.rowMobile} style={animStyle} onAnimationEnd={handleAnimEnd}>
        <div className={s.mobileTopRow}>
          {thumb}
          <div className={s.rowText}>
            <span className={s.rowTitle}>{song.title}</span>
            <span className={s.rowArtist}>{song.artist}{song.year ? ` · ${song.year}` : ''}</span>
          </div>
        </div>
        <div className={s.instrumentStatusRow} style={{ justifyContent: 'center' }}>
          {instrumentChips!.map(c => (
            <div key={c.key} className={s.instrumentStatusChip} style={{ backgroundColor: c.fill, borderColor: c.stroke }}>
              <InstrumentIcon instrument={c.key} size={24} />
            </div>
          ))}
        </div>
      </Link>
    );
  }

  return (
    <Link ref={linkRef} to={`/songs/${song.songId}${instrumentFilter != null ? `?instrument=${encodeURIComponent(instrument)}` : ''}`} state={{ backTo: location.pathname }} className={s.row} style={animStyle} onAnimationEnd={handleAnimEnd}>
      {thumb}
      <div className={s.rowText}>
        <span className={s.rowTitle}>{song.title}</span>
        <span className={s.rowArtist}>{song.artist}{song.year ? ` · ${song.year}` : ''}</span>
      </div>
      {chipRow}
      {entries.length > 0 && (
        <div className={s.scoreMeta}>
          {entries.map(e => <Fragment key={e.key}>{e.el}</Fragment>)}
        </div>
      )}
    </Link>
  );
});
