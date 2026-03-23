/**
 * First-run demo: A single CategoryCard that rotates between category types
 * every 5 seconds with a fade-out/in transition. Uses real songs from the catalog.
 */
import { useState, useEffect, useMemo, useRef, useCallback } from 'react';
import type { SuggestionCategory, SuggestionSongItem } from '@festival/core/suggestions/types';
import { CategoryCard } from '../../components/CategoryCard';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { FADE_DURATION, DEMO_SWAP_INTERVAL_MS } from '@festival/theme';
import css from './CategoryCardDemo.module.css';

// Card header (padding + content + border) + card bottom margin
const CARD_OVERHEAD = 96;
const SONG_ROW_HEIGHT = 52;
const MAX_DEMO_SONGS = 5;

type CardTemplate = {
  key: string;
  title: string;
  description: string;
  songMeta: (i: number) => Partial<SuggestionSongItem>;
};

const TEMPLATES: CardTemplate[] = [
  {
    key: 'unfc_guitar', title: 'Finish the Guitar FCs',
    description: 'Play these songs again on Guitar and grab an FC!',
    songMeta: (i) => ({ percent: 100 - i * 2, instrumentKey: 'guitar' as const }),
  },
  {
    key: 'pct_push_bass', title: 'Percentile Push: Bass',
    description: 'Replay these Bass songs to jump to the next percentile bracket.',
    /* v8 ignore next -- songMeta only called when timer rotates to this template */
    songMeta: (i) => ({ percentileDisplay: `Top ${3 + i}%`, instrumentKey: 'bass' as const }),
  },
  {
    key: 'near_fc_any', title: 'FC These Next!',
    description: 'If you can get gold stars, you can FC it!',
    /* v8 ignore next -- songMeta only called when timer rotates to this template */
    songMeta: (i) => ({ instrumentKey: (['guitar', 'bass', 'drums', 'vocals'] as const)[i % 4] }),
  },
  {
    key: 'unplayed_drums', title: 'New on Drums',
    description: "Songs you haven't played on Drums yet.",
    /* v8 ignore next -- songMeta only called when timer rotates to this template */
    songMeta: () => ({ instrumentKey: 'drums' as const }),
  },
];

export default function CategoryCardDemo() {
  const h = useSlideHeight();
  const { state: { songs: apiSongs } } = useFestival();
  const [templateIdx, setTemplateIdx] = useState(0);
  const [visible, setVisible] = useState(false);
  const idxRef = useRef(0);

  // Mount: start hidden, transition to visible on next frame
  /* v8 ignore start -- rAF callback depends on real browser frame scheduling */
  useEffect(() => {
    const raf = requestAnimationFrame(() => setVisible(true));
    return () => cancelAnimationFrame(raf);
  }, []);
  /* v8 ignore stop */

  const maxSongs = h
    ? Math.min(MAX_DEMO_SONGS, Math.max(1, Math.floor((h - CARD_OVERHEAD) / SONG_ROW_HEIGHT)))
    : MAX_DEMO_SONGS;

  // Rotate every DEMO_SWAP_INTERVAL_MS: fade out → swap → fade in
  /* v8 ignore start -- timer/rAF swap cycle depends on real browser scheduling */
  const rotate = useCallback(() => {
    setVisible(false);
    setTimeout(() => {
      idxRef.current = (idxRef.current + 1) % TEMPLATES.length;
      setTemplateIdx(idxRef.current);
      // Wait a frame so the DOM updates before transitioning back in
      requestAnimationFrame(() => setVisible(true));
    }, FADE_DURATION);
  }, []);
  /* v8 ignore stop */

  useEffect(() => {
    const timer = setInterval(rotate, DEMO_SWAP_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [rotate]);

  const { category, albumArtMap } = useMemo(() => {
    const tmpl = TEMPLATES[templateIdx]!;
    const pool = apiSongs.filter(s => s.albumArt);
    const start = templateIdx * MAX_DEMO_SONGS;
    /* v8 ignore start -- pool always has songs when apiSongs has albumArt */
    const songPool = pool.length > 0
      ? Array.from({ length: maxSongs }, (_, i) => pool[(start + i) % pool.length]!)
      : [];
    /* v8 ignore stop */
    const demoSongs: SuggestionSongItem[] = songPool.map((s, i) => ({
      songId: s.songId,
      title: s.title,
      artist: s.artist,
      year: s.year,
      ...tmpl.songMeta(i),
    }));
    const artMap = new Map<string, string>();
    for (const s of pool) {
      /* v8 ignore start -- pool is pre-filtered to songs with albumArt */
      if (s.albumArt) { artMap.set(s.songId, s.albumArt); }
      /* v8 ignore stop */
    }
    const cat: SuggestionCategory = {
      key: tmpl.key,
      title: tmpl.title,
      description: tmpl.description,
      songs: demoSongs,
    };
    return { category: cat, albumArtMap: artMap };
  }, [apiSongs, maxSongs, templateIdx]);

  const emptyScores = useMemo(() => ({}), []);

  return (
    /* v8 ignore start -- visible state depends on rAF callback */
    <div className={`${css.wrapper} ${visible ? css.visible : css.hidden}`}>
    {/* v8 ignore stop */}
      <CategoryCard category={category} albumArtMap={albumArtMap} scoresIndex={emptyScores} />
    </div>
  );
}
