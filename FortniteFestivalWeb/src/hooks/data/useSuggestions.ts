import { useState, useRef, useCallback, useEffect } from 'react';
import { SuggestionGenerator } from '@festival/core/suggestions/suggestionGenerator';
import type { SuggestionCategory } from '@festival/core/suggestions/types';
import type { Song as CoreSong, LeaderboardData } from '@festival/core/models';

const BATCH_SIZE = 6;
const INITIAL_BATCH = 10;

// Module-level cache so suggestions survive navigation
let _cache: {
  accountId: string;
  categories: SuggestionCategory[];
  generator: SuggestionGenerator;
  scrollY: number;
} | null = null;

export function useSuggestions(
  accountId: string,
  coreSongs: CoreSong[],
  scoresIndex: Record<string, LeaderboardData>,
  currentSeason = 0,
) {
  // Restore from cache if same account
  const cached = _cache?.accountId === accountId ? _cache : null;

  const [categories, setCategories] = useState<SuggestionCategory[]>(
    () => cached?.categories ?? [],
  );
  const [hasMore, setHasMore] = useState(true);
  const generatorRef = useRef<SuggestionGenerator | null>(cached?.generator ?? null);
  const readyRef = useRef(!!cached);
  const initializedRef = useRef(!!cached);

  // Restore scroll position after mount — read from _cache directly
  // so it picks up the latest value even after StrictMode double-render
  useEffect(() => {
    const scrollY = _cache?.accountId === accountId ? _cache.scrollY : 0;
    /* v8 ignore start — scroll position restore */
    if (scrollY > 0) {
      /* v8 ignore start */
      const t1 = setTimeout(() => window.scrollTo(0, scrollY), 0);
      const t2 = setTimeout(() => window.scrollTo(0, scrollY), 100);
      return () => { clearTimeout(t1); clearTimeout(t2); };
      /* v8 ignore stop */
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Continuously save scroll position so browser back works
  useEffect(() => {
    /* v8 ignore start — scroll position tracking */
    const onScroll = () => {
      if (_cache?.accountId === accountId && window.scrollY > 0) {
        _cache.scrollY = window.scrollY;
      /* v8 ignore stop */
      }
    };
    window.addEventListener('scroll', onScroll, { passive: true });
    return () => window.removeEventListener('scroll', onScroll);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Initialize generator once when source data is ready
  useEffect(() => {
    if (coreSongs.length === 0) {
      // Don't reset readyRef if we have a cached generator
      if (!initializedRef.current) readyRef.current = false;
      return;
    }
    if (initializedRef.current) return;
    initializedRef.current = true;

    const gen = new SuggestionGenerator({ seed: Date.now(), currentSeason });
    gen.setSource(coreSongs, scoresIndex);
    generatorRef.current = gen;
    readyRef.current = true;

    const first = gen.getNext(INITIAL_BATCH);
    setCategories(first);
    setHasMore(true);

    _cache = { accountId, categories: first, generator: gen, scrollY: 0 };
  }, [accountId, coreSongs, scoresIndex]);

  const loadMore = useCallback(() => {
    const gen = generatorRef.current;
    if (!gen || !readyRef.current) return;

    let next = gen.getNext(BATCH_SIZE);
    if (next.length === 0) {
      gen.resetForEndless();
      next = gen.getNext(BATCH_SIZE);
    }
    if (next.length === 0) {
      setHasMore(false);
      return;
    }
    setCategories((prev) => {
      const updated = [...prev, ...next];
      if (_cache) _cache.categories = updated;
      return updated;
    });
  }, []);

  return { categories, loadMore, hasMore };
}
