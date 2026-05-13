import { useState, useRef, useCallback, useEffect } from 'react';
import { SuggestionGenerator } from '@festival/core/suggestions/suggestionGenerator';
import type { SuggestionCategory } from '@festival/core/suggestions/types';
import type { Song as CoreSong, LeaderboardData } from '@festival/core/models';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { api } from '../../api/client';
import { buildRivalDataIndexFromRivalsAll } from '../../utils/suggestionAdapter';
import { deriveComboFromSettings } from '../../pages/rivals/helpers/comboUtils';
import { useSettings } from '../../contexts/SettingsContext';

const BATCH_SIZE = 6;
const INITIAL_BATCH = 10;

type SuggestionsMode = 'solo' | 'band';

type UseSuggestionsOptions = {
  mode?: SuggestionsMode;
  cacheKey?: string;
  sourceReady?: boolean;
  bandComboId?: string | null;
};

// Module-level cache so suggestions survive navigation
let _cache: {
  cacheKey: string;
  categories: SuggestionCategory[];
  generator: SuggestionGenerator;
  scrollY: number;
} | null = null;

export function useSuggestions(
  accountId: string,
  coreSongs: CoreSong[],
  scoresIndex: Record<string, LeaderboardData>,
  currentSeason = 0,
  options: UseSuggestionsOptions = {},
) {
  const { settings } = useSettings();
  const mode = options.mode ?? 'solo';
  const cacheKey = options.cacheKey ?? `${mode}:${accountId}`;
  const sourceReady = options.sourceReady ?? true;

  // Restore from cache if same suggestion identity
  const cached = _cache?.cacheKey === cacheKey ? _cache : null;

  const [categories, setCategories] = useState<SuggestionCategory[]>(
    () => cached?.categories ?? [],
  );
  const [hasMore, setHasMore] = useState(true);
  const generatorRef = useRef<SuggestionGenerator | null>(cached?.generator ?? null);
  const readyRef = useRef(!!cached);
  const initializedRef = useRef(!!cached);
  const cacheKeyRef = useRef(cacheKey);
  const rivalDataInjectedRef = useRef(false);

  useEffect(() => {
    if (cacheKeyRef.current === cacheKey) return;

    cacheKeyRef.current = cacheKey;
    const nextCached = _cache?.cacheKey === cacheKey ? _cache : null;
    setCategories(nextCached?.categories ?? []);
    setHasMore(true);
    generatorRef.current = nextCached?.generator ?? null;
    readyRef.current = !!nextCached;
    initializedRef.current = !!nextCached;
    rivalDataInjectedRef.current = false;
  }, [cacheKey]);

  // Restore scroll position after mount — read from _cache directly
  // so it picks up the latest value even after StrictMode double-render
  const scrollContainerRef = useScrollContainer();
  useEffect(() => {
    const scrollY = _cache?.cacheKey === cacheKey ? _cache.scrollY : 0;
    /* v8 ignore start — scroll position restore */
    if (scrollY > 0) {
      /* v8 ignore start */
      const t1 = setTimeout(() => scrollContainerRef.current?.scrollTo(0, scrollY), 0);
      const t2 = setTimeout(() => scrollContainerRef.current?.scrollTo(0, scrollY), 100);
      return () => { clearTimeout(t1); clearTimeout(t2); };
      /* v8 ignore stop */
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cacheKey]);

  // Continuously save scroll position so browser back works
  useEffect(() => {
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl) return;
    /* v8 ignore start — scroll position tracking */
    const onScroll = () => {
      if (_cache?.cacheKey === cacheKey && scrollEl.scrollTop > 0) {
        _cache.scrollY = scrollEl.scrollTop;
      /* v8 ignore stop */
      }
    };
    scrollEl.addEventListener('scroll', onScroll, { passive: true });
    return () => scrollEl.removeEventListener('scroll', onScroll);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cacheKey]);

  // Initialize generator once when source data is ready
  useEffect(() => {
    if (!sourceReady) {
      if (!initializedRef.current) readyRef.current = false;
      return;
    }
    if (coreSongs.length === 0) {
      // Don't reset readyRef if we have a cached generator
      if (!initializedRef.current) readyRef.current = false;
      return;
    }
    if (initializedRef.current) return;
    initializedRef.current = true;

    const gen = new SuggestionGenerator({
      seed: Date.now(),
      currentSeason,
      mode,
      bandComboId: options.bandComboId,
    });
    gen.setSource(coreSongs, scoresIndex);
    generatorRef.current = gen;
    readyRef.current = true;

    const first = gen.getNext(INITIAL_BATCH);
    setCategories(first);
    setHasMore(true);

    _cache = { cacheKey, categories: first, generator: gen, scrollY: 0 };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- currentSeason/options only needed at init
  }, [accountId, cacheKey, coreSongs, scoresIndex, sourceReady]);

  // Fetch rival data and inject into the generator when it becomes ready.
  useEffect(() => {
    if (mode !== 'solo') return;
    if (!accountId) return;
    if (!generatorRef.current || rivalDataInjectedRef.current) return;

    let cancelled = false;
    const combo = deriveComboFromSettings(settings) ?? undefined;

    api.getRivalsAll(accountId)
      .then((response) => {
        if (cancelled || !generatorRef.current) return;
        const index = buildRivalDataIndexFromRivalsAll(response, combo, 5);
        generatorRef.current.setRivalData(index);
        rivalDataInjectedRef.current = true;
      })
      .catch(() => {
        // Graceful degradation — rival suggestions simply won't appear
      });

    return () => { cancelled = true; };
  // eslint-disable-next-line react-hooks/exhaustive-deps -- inject once after generator init using the current settings snapshot
  }, [accountId, mode, settings, generatorRef.current]);

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
