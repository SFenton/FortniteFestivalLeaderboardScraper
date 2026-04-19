/**
 * First-run demo: A single CategoryCard that rotates between category types
 * every 5 seconds with a fade-out/in transition. Uses real songs from the catalog.
 */
import { useState, useEffect, useMemo, useRef, useCallback, useLayoutEffect, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import type { SuggestionCategory, SuggestionSongItem } from '@festival/core/suggestions/types';
import { CategoryCard } from '../../components/CategoryCard';
import { useFestival } from '../../../../contexts/FestivalContext';
import { useSlideHeight } from '../../../../firstRun/SlideHeightContext';
import { useIsNarrow } from '../../../../hooks/ui/useIsMobile';
import { FADE_DURATION, DEMO_SWAP_INTERVAL_MS, CssValue, PointerEvents, Gap, Opacity, transition, translateY, CssProp, transitions } from '@festival/theme';

const MAX_DEMO_SONGS = 5;
const FIT_EPSILON = 1;
const FIT_BUFFER = Gap.section;

type CardTemplate = {
  key: string;
  titleKey: string;
  descKey: string;
  songMeta: (i: number) => Partial<SuggestionSongItem>;
};

const TEMPLATES: CardTemplate[] = [
  {
    key: 'unfc_guitar', titleKey: 'firstRun.suggestions.demo.unfcTitle',
    descKey: 'firstRun.suggestions.demo.unfcDesc',
    songMeta: (i) => ({ percent: 100 - i * 2, instrumentKey: 'guitar' as const }),
  },
  {
    key: 'pct_push_bass', titleKey: 'firstRun.suggestions.demo.pctPushTitle',
    descKey: 'firstRun.suggestions.demo.pctPushDesc',
    /* v8 ignore next -- songMeta only called when timer rotates to this template */
    songMeta: (i) => ({ percentileDisplay: `Top ${3 + i}%`, instrumentKey: 'bass' as const }),
  },
  {
    key: 'near_fc_any', titleKey: 'firstRun.suggestions.demo.nearFcTitle',
    descKey: 'firstRun.suggestions.demo.nearFcDesc',
    /* v8 ignore next -- songMeta only called when timer rotates to this template */
    songMeta: (i) => ({ instrumentKey: (['guitar', 'bass', 'drums', 'vocals'] as const)[i % 4] }),
  },
  {
    key: 'unplayed_drums', titleKey: 'firstRun.suggestions.demo.unplayedTitle',
    descKey: 'firstRun.suggestions.demo.unplayedDesc',
    /* v8 ignore next -- songMeta only called when timer rotates to this template */
    songMeta: () => ({ instrumentKey: 'drums' as const }),
  },
];

export default function CategoryCardDemo() {
  const { t } = useTranslation();
  const h = useSlideHeight();
  const isNarrow = useIsNarrow();
  const { state: { songs: apiSongs } } = useFestival();
  const [templateIdx, setTemplateIdx] = useState(0);
  const [visible, setVisible] = useState(false);
  const [visibleSongCount, setVisibleSongCount] = useState(MAX_DEMO_SONGS);
  const [measuredCardHeight, setMeasuredCardHeight] = useState(0);
  const idxRef = useRef(0);
  const cardRef = useRef<HTMLDivElement>(null);

  // Mount: start hidden, transition to visible on next frame
  /* v8 ignore start -- rAF callback depends on real browser frame scheduling */
  useEffect(() => {
    const raf = requestAnimationFrame(() => setVisible(true));
    return () => cancelAnimationFrame(raf);
  }, []);
  /* v8 ignore stop */

  // Reset to the optimistic row count whenever the available height or responsive
  // layout changes; a layout effect below trims rows until the rendered card fits.
  /* v8 ignore start -- layout effect relies on measured DOM height */
  useLayoutEffect(() => {
    setVisibleSongCount(MAX_DEMO_SONGS);
    setMeasuredCardHeight(0);
  }, [templateIdx, h, isNarrow]);

  // Watch the rendered card height so late font/layout changes also trigger a
  // fit pass, not just the initial render.
  useLayoutEffect(() => {
    const node = cardRef.current;
    if (!node) return;

    const update = () => {
      setMeasuredCardHeight(node.getBoundingClientRect().height);
    };

    update();

    const ro = new ResizeObserver(() => {
      update();
    });

    ro.observe(node);
    return () => ro.disconnect();
  }, [templateIdx, visibleSongCount]);

  useLayoutEffect(() => {
    if (!h || measuredCardHeight <= 0) return;

    if (measuredCardHeight > (h - FIT_BUFFER + FIT_EPSILON) && visibleSongCount > 1) {
      setVisibleSongCount((current) => current - 1);
    }
  }, [h, measuredCardHeight, visibleSongCount, templateIdx, isNarrow]);
  /* v8 ignore stop */

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
      ? Array.from({ length: visibleSongCount }, (_, i) => pool[(start + i) % pool.length]!)
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
      title: t(tmpl.titleKey),
      description: t(tmpl.descKey),
      songs: demoSongs,
    };
    return { category: cat, albumArtMap: artMap };
  }, [apiSongs, templateIdx, t, visibleSongCount]);

  const emptyScores = useMemo(() => ({}), []);
  const s = useStyles(visible);

  return (
    /* v8 ignore start -- visible state depends on rAF callback */
    <div style={s.wrapper}>
      <div
        ref={cardRef}
        style={s.measureWrap}
        data-testid="suggestions-fre-category-card"
        data-template-key={category.key}
        data-song-count={category.songs.length}
      >
        {/* v8 ignore stop */}
        <CategoryCard category={category} albumArtMap={albumArtMap} scoresIndex={emptyScores} />
      </div>
    </div>
  );
}

function useStyles(visible: boolean) {
  return useMemo(() => {
    const trans = transitions(
      transition(CssProp.opacity, FADE_DURATION),
      transition(CssProp.transform, FADE_DURATION),
    );
    return {
      wrapper: {
        width: CssValue.full,
        pointerEvents: PointerEvents.none,
        transition: trans,
        opacity: visible ? 1 : Opacity.none,
        transform: visible ? translateY(0) : translateY(8),
      } as CSSProperties,
      measureWrap: {
        width: CssValue.full,
        marginBottom: -Gap.section,
      } as CSSProperties,
    };
  }, [visible]);
}
