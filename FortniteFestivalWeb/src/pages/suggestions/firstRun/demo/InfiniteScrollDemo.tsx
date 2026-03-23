/**
 * First-run demo: Auto-scrolling list of CategoryCards using transform animation.
 * Uses translateY to scroll content within a clipped viewport, bypassing
 * the carousel's flex layout which prevents real scroll overflow.
 */
import { useEffect, useRef, useMemo, useCallback } from 'react';
import { useTranslation } from 'react-i18next';
import type { SuggestionCategory, SuggestionSongItem } from '@festival/core/suggestions/types';
import { CategoryCard } from '../../components/CategoryCard';
import FadeIn from '../../../../components/page/FadeIn';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { STAGGER_INTERVAL } from '@festival/theme';
import css from './InfiniteScrollDemo.module.css';

type CategoryTemplate = {
  key: string;
  titleKey: string;
  descKey: string;
  songMeta: (i: number) => Partial<SuggestionSongItem>;
};

const CATEGORY_TEMPLATES: CategoryTemplate[] = [
  {
    key: 'unfc_guitar', titleKey: 'firstRun.suggestions.demo.unfcTitle',
    descKey: 'firstRun.suggestions.demo.unfcDesc',
    songMeta: (i) => ({ percent: 100 - i * 2, instrumentKey: 'guitar' as const }),
  },
  {
    key: 'pct_push_bass', titleKey: 'firstRun.suggestions.demo.pctPushTitle',
    descKey: 'firstRun.suggestions.demo.pctPushDesc',
    songMeta: (i) => ({ percentileDisplay: `Top ${3 + i}%`, instrumentKey: 'bass' as const }),
  },
  {
    key: 'stale_vocals_1', titleKey: 'firstRun.suggestions.demo.staleTitle',
    descKey: 'firstRun.suggestions.demo.staleDesc',
    songMeta: () => ({ instrumentKey: 'vocals' as const }),
  },
  {
    key: 'near_fc_any', titleKey: 'firstRun.suggestions.demo.nearFcTitle',
    descKey: 'firstRun.suggestions.demo.nearFcDesc',
    songMeta: (i) => ({ instrumentKey: (['guitar', 'bass', 'drums', 'vocals'] as const)[i % 4] }),
  },
  {
    key: 'unplayed_drums', titleKey: 'firstRun.suggestions.demo.unplayedTitle',
    descKey: 'firstRun.suggestions.demo.unplayedDesc',
    songMeta: () => ({ instrumentKey: 'drums' as const }),
  },
  {
    key: 'variety_pack', titleKey: 'firstRun.suggestions.demo.varietyTitle',
    descKey: 'firstRun.suggestions.demo.varietyDesc',
    songMeta: () => ({}),
  },
];

const SONGS_PER_CARD = 2;
const SCROLL_SPEED_PX_PER_SEC = 30;

export default function InfiniteScrollDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const { state: { songs: apiSongs } } = useFestival();
  const innerRef = useRef<HTMLDivElement>(null);
  const viewportRef = useRef<HTMLDivElement>(null);
  const rafRef = useRef(0);
  const offsetRef = useRef(0);

  // Set viewport height from slide context
  useEffect(() => {
    if (viewportRef.current && h) {
      viewportRef.current.style.height = `${h}px`;
    }
  }, [h]);

  const { categories, albumArtMap } = useMemo(() => {
    const pool = apiSongs.filter(s => s.albumArt);
    const artMap = new Map<string, string>();
    for (const s of pool) {
      /* v8 ignore start -- pool is pre-filtered to songs with albumArt */
      if (s.albumArt) { artMap.set(s.songId, s.albumArt); }
      /* v8 ignore stop */
    }
    const cats: SuggestionCategory[] = CATEGORY_TEMPLATES.map((tmpl, ci) => {
      const start = ci * SONGS_PER_CARD;
      const songSlice = pool.slice(start, start + SONGS_PER_CARD);
      const songs: SuggestionSongItem[] = songSlice.map((s, si) => ({
        songId: s.songId,
        title: s.title,
        artist: s.artist,
        year: s.year,
        ...tmpl.songMeta(si),
      }));
      return { key: tmpl.key, title: t(tmpl.titleKey), description: t(tmpl.descKey), songs };
    });
    return { categories: cats, albumArtMap: artMap };
  }, [apiSongs, t]);

  const emptyScores = useMemo(() => ({}), []);

  // Animate via transform: translateY — works inside the carousel's flex layout
  /* v8 ignore start -- rAF animation loop depends on real browser frame scheduling */
  const animate = useCallback(() => {
    const inner = innerRef.current;
    const viewport = viewportRef.current;
    if (!inner || !viewport || !h) return;
    const contentHeight = inner.scrollHeight;
    const maxOffset = contentHeight - h;
    if (maxOffset <= 0) return;

    let lastTime = 0;
    const step = (time: number) => {
      if (lastTime) {
        const dt = (time - lastTime) / 1000;
        offsetRef.current += SCROLL_SPEED_PX_PER_SEC * dt;
        if (offsetRef.current >= maxOffset) {
          offsetRef.current = 0;
        }
        inner.style.transform = `translateY(${-offsetRef.current}px)`;

        // Update fade mask class
        const atTop = offsetRef.current <= 0;
        const atBottom = offsetRef.current >= maxOffset - 1;
        viewport.className = `${css.viewport} ${
          atTop && atBottom ? '' : atTop ? css.fadeBottom : atBottom ? css.fadeTop : css.fadeBoth
        }`;
      }
      lastTime = time;
      rafRef.current = requestAnimationFrame(step);
    };
    rafRef.current = requestAnimationFrame(step);
  }, [h]);
  /* v8 ignore stop */

  useEffect(() => {
    if (categories.length === 0 || !h) return;
    // Start scrolling immediately — runs in parallel with FadeIn stagger
    const id = setTimeout(animate, 100);
    return () => { clearTimeout(id); cancelAnimationFrame(rafRef.current); };
  }, [categories.length, h, animate]);

  // Edge fade masks — applied via className in animation loop
  /* v8 ignore start -- initialMask branches depend on animation state not reachable in tests */
  const atTop = offsetRef.current <= 0;
  const contentHeight = innerRef.current?.scrollHeight ?? 0;
  const maxOffset = h ? contentHeight - h : 0;
  const atBottom = maxOffset <= 0 || offsetRef.current >= maxOffset - 1;
  const initialMask = atTop && atBottom ? '' : atTop ? css.fadeBottom : atBottom ? css.fadeTop : css.fadeBoth;
  /* v8 ignore stop */

  return (
    <div ref={viewportRef} className={`${css.viewport} ${initialMask}`}>
      <div ref={innerRef} className={css.innerTrack}>
        {categories.map((cat, i) => (
          <FadeIn key={i} delay={i * STAGGER_INTERVAL}>
            <CategoryCard category={cat} albumArtMap={albumArtMap} scoresIndex={emptyScores} />
          </FadeIn>
        ))}
      </div>
    </div>
  );
}
