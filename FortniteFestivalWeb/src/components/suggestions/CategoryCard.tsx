/**
 * Suggestion category and song row components extracted from SuggestionsPage.
 */
import { memo } from 'react';
import { Link } from 'react-router-dom';
import { InstrumentKeys, type InstrumentKey } from '@festival/core/instruments';
import type { LeaderboardData } from '@festival/core/models';
import type { SuggestionCategory, SuggestionSongItem } from '@festival/core/suggestions/types';
import { InstrumentIcon, getInstrumentStatusVisual } from '../InstrumentIcons';
import AlbumArt from '../AlbumArt';
import SeasonPill from '../SeasonPill';
import { Size } from '@festival/theme';
import { useIsMobile } from '../../hooks/useIsMobile';
import s from '../../pages/SuggestionsPage.module.css';

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

type RowLayout = 'instrumentChips' | 'singleInstrument' | 'percentile' | 'season' | 'unfcAccuracy' | 'hidden';

function getRowLayout(categoryKey: string): RowLayout {
  const k = categoryKey.toLowerCase();
  if (k.startsWith('variety_pack') || k.startsWith('artist_sampler_') || k.startsWith('artist_unplayed_')
    || k.startsWith('unplayed_')
    || (k.startsWith('samename_') && !k.startsWith('samename_nearfc_'))) return 'hidden';
  if (k.startsWith('unfc_')) return 'unfcAccuracy';
  if (k.startsWith('stale_')) return 'season';
  if (k.startsWith('almost_elite') || k.startsWith('pct_push') || k.startsWith('pct_improve') || k.startsWith('same_pct') || k.startsWith('improve_rankings')) return 'percentile';
  if (k.startsWith('near_fc') || k.startsWith('almost_six_star') || k.startsWith('more_stars')
    || k.startsWith('first_plays_mixed') || k.startsWith('star_gains')
    || k.startsWith('samename_nearfc_')) return 'singleInstrument';
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
  const catInstrument = getCatInstrument(category.key);
  return (
    <div className={s.card}>
      <div className={s.cardHeader}>
        <div className={s.cardHeaderRow}>
          <div>
            <span className={s.cardTitle}>{category.title}</span>
            <span className={s.cardDesc}>{category.description}</span>
          </div>
          {catInstrument && <InstrumentIcon instrument={catInstrument} size={36} />}
        </div>
      </div>
      <div className={s.songList}>
        {category.songs.map((song) => (
          <SongRow
            key={`${song.songId}-${song.instrumentKey ?? 'any'}`}
            song={song}
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

export function SongRow({
  song, categoryKey, albumArt, leaderboardData,
}: {
  song: SuggestionSongItem;
  categoryKey: string;
  albumArt?: string;
  leaderboardData?: LeaderboardData;
}) {
  const layout = getRowLayout(categoryKey);
  const isMobile = useIsMobile();
  const starCount = song.stars ?? 0;
  const isGold = starCount >= 6;
  const displayStars = isGold ? 5 : starCount;
  const showStars = layout === 'singleInstrument' && categoryKey.startsWith('star_gains') && starCount > 0;
  const showStarPngs = showStars && isMobile;
  const instrument = song.instrumentKey ?? getCatInstrument(categoryKey);
  const songUrl = instrument
    ? `/songs/${song.songId}?instrument=${CORE_TO_SERVER_INSTRUMENT[instrument]}`
    : `/songs/${song.songId}`;
  const starSrc = isGold ? `${BASE}star_gold.png` : `${BASE}star_white.png`;

  return (
    <Link to={songUrl} className={s.row} style={showStarPngs ? { flexDirection: 'column', alignItems: 'stretch' } : undefined}>
      <div className={showStarPngs ? s.rowMainLine : undefined} style={!showStarPngs ? { display: 'contents' } : undefined}>
        <AlbumArt src={albumArt} size={Size.thumb} />
        <div className={s.rowText}>
          <span className={s.rowTitle}>{song.title}</span>
          <span className={s.rowArtist}>{song.artist}{song.year ? ` · ${song.year}` : ''}</span>
        </div>
        <RightContent song={song} layout={layout} leaderboardData={leaderboardData} starCount={showStars && !showStarPngs ? displayStars : 0} starSrc={starSrc} />
      </div>
      {showStarPngs && (
        <div className={s.starPngRow}>
          {Array.from({ length: displayStars }, (_, i) => (
            <img key={i} src={starSrc} alt="★" className={s.starPngImg} />
          ))}
        </div>
      )}
    </Link>
  );
}

// ── RightContent ──

function RightContent({
  song, layout, leaderboardData, starCount = 0, starSrc,
}: {
  song: SuggestionSongItem;
  layout: RowLayout;
  leaderboardData?: LeaderboardData;
  starCount?: number;
  starSrc?: string;
}) {
  if (layout === 'hidden') return null;

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
    return <span className={s.unfcPct} style={{ color: `rgb(${r},${g},${b})` }}>{display}</span>;
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
    const isTop1 = display === 'Top 1%';
    const isTop5 = display === 'Top 2%' || display === 'Top 3%' || display === 'Top 4%' || display === 'Top 5%';
    const pillClass = isTop1 ? s.percentileBadgeTop1 : isTop5 ? s.percentileBadgeTop5 : s.percentilePill;
    return (
      <div className={s.badges}>
        {display && <span className={pillClass}>{display}</span>}
        {song.instrumentKey && <InstrumentIcon instrument={song.instrumentKey} size={28} />}
      </div>
    );
  }

  if (layout === 'singleInstrument') {
    return song.instrumentKey ? (
      <div className={s.badges}>
        {starCount > 0 && starSrc && (
          <span className={s.starPngInlineRow}>
            {Array.from({ length: starCount }, (_, i) => (
              <img key={i} src={starSrc} alt="★" className={s.starPngImg} />
            ))}
          </span>
        )}
        <InstrumentIcon instrument={song.instrumentKey} size={28} />
      </div>
    ) : null;
  }

  // instrumentChips
  return (
    <div className={s.instrumentChipsRow}>
      {InstrumentKeys.map((ins) => {
        const tr = leaderboardData ? (leaderboardData as Record<string, unknown>)[ins] as { numStars?: number; isFullCombo?: boolean } | undefined : undefined;
        const hasScore = !!tr && (tr.numStars ?? 0) > 0;
        const isFC = !!tr?.isFullCombo;
        const { fill, stroke } = getInstrumentStatusVisual(hasScore, isFC);
        return (
          <div key={ins} className={s.instrumentChip} style={{ backgroundColor: fill, borderColor: stroke }}>
            <InstrumentIcon instrument={ins} size={20} />
          </div>
        );
      })}
    </div>
  );
}
