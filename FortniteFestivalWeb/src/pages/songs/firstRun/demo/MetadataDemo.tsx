/**
 * First-run demo: song rows with varied metadata pill combos.
 * Desktop: fills as many rows as the slide area allows via SlideHeightContext.
 * Every 5 s one random row fades out, swaps to a song with a *different*
 * metadata layout, and fades back in.
 */
import { useState, useEffect, useMemo, useRef } from 'react';
import SongInfo from '../../../../components/songs/metadata/SongInfo';
import ScorePill from '../../../../components/songs/metadata/ScorePill';
import AccuracyDisplay from '../../../../components/songs/metadata/AccuracyDisplay';
import MiniStars from '../../../../components/songs/metadata/MiniStars';
import PercentilePill from '../../../../components/songs/metadata/PercentilePill';
import SeasonPill from '../../../../components/songs/metadata/SeasonPill';
import DifficultyBars from '../../../../components/songs/metadata/DifficultyBars';
import { useIsMobile } from '../../../../hooks/ui/useIsMobile';
import { DEMO_SWAP_INTERVAL_MS, Layout } from '@festival/theme';
import type { SongDisplay as DemoSong } from '@festival/core/api/serverTypes';
import { useDemoSongs, FADE_MS, shuffle } from '../../../../hooks/data/useDemoSongs';
import { DemoSongRow } from './DemoSongRow';
import { scoreMeta, metadataWrap, mobileTopRow, detailStrip } from '../../../../styles/songRowStyles';

/* ── Song pool comes from useDemoSongs hook ── */

/* ── Metadata layouts ── */

enum MetadataLayout { ScoreAccuracy, StarsDifficulty, PercentileSeason, ScoreStars, AccuracyDifficulty, PercentileScore }

interface MetaValues { score: number; accuracy: number; fc: boolean; stars: number; percentile: string; season: number; difficulty: number }

const ALL_LAYOUTS: MetadataLayout[] = [MetadataLayout.ScoreAccuracy, MetadataLayout.StarsDifficulty, MetadataLayout.PercentileSeason, MetadataLayout.ScoreStars, MetadataLayout.AccuracyDifficulty, MetadataLayout.PercentileScore];

/** Fake but realistic per-song metadata — use non-round accuracy values so decimals show. */
const META_DATA: MetaValues[] = [
  { score: 198942, accuracy: 1000000, fc: true,  stars: 6, percentile: 'Top 1%',  season: 12, difficulty: 4 },
  { score: 157320, accuracy: 980000, fc: false, stars: 5, percentile: 'Top 5%',  season: 10, difficulty: 3 },
  { score: 142800, accuracy: 960000, fc: false, stars: 5, percentile: 'Top 8%',  season: 11, difficulty: 5 },
  { score: 185600, accuracy: 1000000, fc: true,  stars: 6, percentile: 'Top 2%',  season: 9,  difficulty: 2 },
  { score: 123400, accuracy: 940000, fc: false, stars: 4, percentile: 'Top 15%', season: 8,  difficulty: 4 },
  { score: 176100, accuracy: 1000000, fc: true,  stars: 6, percentile: 'Top 3%',  season: 12, difficulty: 3 },
  { score: 110250, accuracy: 910000, fc: false, stars: 4, percentile: 'Top 20%', season: 7,  difficulty: 5 },
  { score: 168900, accuracy: 970000, fc: false, stars: 5, percentile: 'Top 6%',  season: 11, difficulty: 2 },
  { score: 191200, accuracy: 1000000, fc: true,  stars: 6, percentile: 'Top 1%',  season: 10, difficulty: 4 },
  { score: 135700, accuracy: 950000, fc: false, stars: 5, percentile: 'Top 10%', season: 9,  difficulty: 3 },
];

function MetadataStrip({ layout, meta }: { layout: MetadataLayout; meta: MetaValues }) {
  const pills: Record<MetadataLayout, React.ReactNode> = {
    [MetadataLayout.ScoreAccuracy]: <><ScorePill score={meta.score} width="78px" bold /><AccuracyDisplay accuracy={meta.accuracy} isFullCombo={meta.fc} /></>,
    [MetadataLayout.StarsDifficulty]: <><MiniStars starsCount={meta.stars} isFullCombo={meta.fc} /><DifficultyBars level={meta.difficulty} raw /></>,
    [MetadataLayout.PercentileSeason]: <><PercentilePill display={meta.percentile} /><SeasonPill season={meta.season} /></>,
    [MetadataLayout.ScoreStars]: <><ScorePill score={meta.score} width="78px" bold /><MiniStars starsCount={meta.stars} isFullCombo={meta.fc} /></>,
    [MetadataLayout.AccuracyDifficulty]: <><AccuracyDisplay accuracy={meta.accuracy} isFullCombo={meta.fc} /><DifficultyBars level={meta.difficulty} raw /></>,
    [MetadataLayout.PercentileScore]: <><PercentilePill display={meta.percentile} /><ScorePill score={meta.score} width="78px" bold /></>,
  };
  return (
    <div style={scoreMeta}>
      {pills[layout]}
    </div>
  );
}

function MobileMetadataStrip({ meta }: { meta: MetaValues }) {
  return (
    <div style={metadataWrap}>
      <AccuracyDisplay accuracy={meta.accuracy} isFullCombo={meta.fc} />
      <MiniStars starsCount={meta.stars} isFullCombo={meta.fc} />
      <PercentilePill display={meta.percentile} />
      <SeasonPill season={meta.season} />
      <DifficultyBars level={meta.difficulty} raw />
    </div>
  );
}

/* ── Sizing ── */

const ROW_HEIGHT_DESKTOP = Layout.demoRowHeight;
const ROW_HEIGHT_MOBILE = Layout.demoRowMobileMetaHeight;

/* v8 ignore start -- pickNewLayout is only called from the v8-ignored swap cycle */
/** Pick a layout not currently shown. */
function pickNewLayout(visible: MetadataLayout[]): MetadataLayout {
  const unused = ALL_LAYOUTS.filter(l => !visible.includes(l));
  const pool = unused.length > 0 ? unused : ALL_LAYOUTS;
  return pool[Math.floor(Math.random() * pool.length)]!;
}
/* v8 ignore stop */

type RowState = { song: DemoSong; meta: MetaValues; layout: MetadataLayout };

export default function MetadataDemo() {
  const isMobile = useIsMobile();
  const { rows: songRows, initialDone, pool: songPool } = useDemoSongs({
    rowHeight: ROW_HEIGHT_DESKTOP,
    mobileRowHeight: ROW_HEIGHT_MOBILE,
    isMobile,
    autoSwap: false,
  });

  const shuffledMeta = useMemo(() => shuffle(META_DATA), []);

  // Mirror songRows but augment with metadata + layout. We manage our own
  // metadata-specific swap cycle that also rotates the layout.
  const [rows, setRows] = useState<RowState[]>([]);
  const [fadingIdx, setFadingIdx] = useState<ReadonlySet<number>>(new Set());
  const lastSwappedRef = useRef<string>('');

  // Sync rows when songRows changes (height change or initial load).
  useEffect(() => {
    setRows(prev => {
      /* v8 ignore start -- stable-data optimization guard; only triggers on re-render with identical songRows */
      if (prev.length === songRows.length && songRows.every((s, i) => prev[i]?.song.title === s.title)) return prev;
      /* v8 ignore stop */
      const next = songRows.map((song, i) => ({
        song,
        meta: shuffledMeta[i % shuffledMeta.length]!,
        layout: ALL_LAYOUTS[i % ALL_LAYOUTS.length]!,
      }));
      return next;
    });
  }, [songRows, shuffledMeta]);

  // Metadata-specific swap cycle: fade-out → swap song+meta+layout → fade-in.
  useEffect(() => {
    if (rows.length === 0) return;

    /* v8 ignore start -- Timer-based swap cycle with random selection; depends on async state transitions */
    const timer = setInterval(() => {
      if (rows.length === 0) return;
      const swapCount = rows.length <= 3 ? 1 : rows.length <= 6 ? 2 : 3;
      const allIndices = Array.from({ length: rows.length }, (_, i) => i);

      let indices: number[];
      let key: string;
      let attempts = 0;
      do {
        const shuffled = shuffle(allIndices);
        indices = shuffled.slice(0, swapCount);
        key = [...indices].sort().join(',');
        attempts++;
      } while (key === lastSwappedRef.current && attempts < 10);
      lastSwappedRef.current = key;

      setFadingIdx(new Set(indices));

      setTimeout(() => {
        setRows(prev => {
          const visibleLayouts = prev.map(r => r.layout);
          const visibleTitles = new Set(prev.map(r => r.song.title));
          const next = [...prev];
          for (const idx of indices) {
            if (idx >= prev.length) continue;
            const newLayout = pickNewLayout(visibleLayouts);
            const available = songPool.filter(s => !visibleTitles.has(s.title));
            const source = available.length > 0 ? available : songPool;
            const newSong = source[Math.floor(Math.random() * source.length)]!;
            const newMeta = shuffledMeta[Math.floor(Math.random() * shuffledMeta.length)]!;
            next[idx] = { song: newSong, meta: newMeta, layout: newLayout };
            visibleTitles.delete(prev[idx]!.song.title);
            visibleTitles.add(newSong.title);
            visibleLayouts[idx] = newLayout;
          }
          return next;
        });
        setFadingIdx(new Set());
      }, FADE_MS);
    }, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
    /* v8 ignore stop */
  }, [rows.length, songPool, shuffledMeta]);

  if (isMobile) {
    return (
      <div>
        {rows.map((row, i) => (
          <DemoSongRow key={i} index={i} initialDone={initialDone} fadingIdx={fadingIdx} mobile>
            <div style={mobileTopRow}>
              <SongInfo albumArt={row.song.albumArt} title={row.song.title} artist={row.song.artist} year={row.song.year} />
              <div style={detailStrip}>
                <ScorePill score={row.meta.score} width="78px" bold />
              </div>
            </div>
            <MobileMetadataStrip meta={row.meta} />
          </DemoSongRow>
        ))}
      </div>
    );
  }

  return (
    <div>
      {rows.map((row, i) => (
        <DemoSongRow key={i} index={i} initialDone={initialDone} fadingIdx={fadingIdx}>
          <SongInfo albumArt={row.song.albumArt} title={row.song.title} artist={row.song.artist} year={row.song.year} />
          <MetadataStrip layout={row.layout} meta={row.meta} />
        </DemoSongRow>
      ))}
    </div>
  );
}
