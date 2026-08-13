import { useState, useRef, useCallback, useEffect, useLayoutEffect } from 'react';
import { SuggestionGenerator } from '@festival/core/suggestions';
import type { SuggestionCategory } from '@festival/core/types';
import type { Song as CoreSong, LeaderboardData } from '@festival/core/types';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { api } from '../../api/client';
import { buildRivalDataIndexFromRivalsAll } from '../../utils/suggestionAdapter';
import { deriveComboFromSettings } from '../../pages/rivals/helpers/comboUtils';
import { useSettings } from '../../contexts/SettingsContext';
import {
  initializeSuggestionsScrollState,
  updateSuggestionsScrollY,
} from '../../pages/suggestions/suggestionsSessionCache';

const BATCH_SIZE = 6;
const INITIAL_BATCH = 10;

type SuggestionsMode = 'solo' | 'band';

type UseSuggestionsOptions = {
  mode?: SuggestionsMode;
  cacheKey?: string;
  sourceReady?: boolean;
  bandComboId?: string | null;
};

// Module-level cache so generated categories survive route navigation.
let suggestionsCache: {
  cacheKey: string;
  categories: SuggestionCategory[];
  generator: SuggestionGenerator;
  loadTriggerCount: number;
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
  const cached = suggestionsCache?.cacheKey === cacheKey
    ? suggestionsCache
    : null;

  const [categories, setCategories] = useState<SuggestionCategory[]>(
    () => cached?.categories ?? [],
  );
  const [hasMore, setHasMore] = useState(true);
  const generatorRef = useRef<SuggestionGenerator | null>(cached?.generator ?? null);
  const readyRef = useRef(!!cached);
  const initializedRef = useRef(!!cached);
  const cacheKeyRef = useRef(cacheKey);
  const rivalDataInjectedRef = useRef(false);
  const loadMorePendingRef = useRef(false);
  const loadTriggerCountRef = useRef(cached?.loadTriggerCount ?? 0);

  useEffect(() => {
    if (cacheKeyRef.current === cacheKey) return;

    cacheKeyRef.current = cacheKey;
    const nextCached = suggestionsCache?.cacheKey === cacheKey
      ? suggestionsCache
      : null;
    setCategories(nextCached?.categories ?? []);
    setHasMore(true);
    generatorRef.current = nextCached?.generator ?? null;
    readyRef.current = !!nextCached;
    initializedRef.current = !!nextCached;
    rivalDataInjectedRef.current = false;
    loadMorePendingRef.current = false;
    loadTriggerCountRef.current = nextCached?.loadTriggerCount ?? 0;
  }, [cacheKey]);

  // Continuously save scroll position so browser back works
  const scrollContainerRef = useScrollContainer();
  useLayoutEffect(() => {
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl) return;
    /* v8 ignore start — scroll position tracking */
    const onScroll = () => {
      const hashPath = window.location.hash.slice(1).split('?')[0];
      if (hashPath !== '/suggestions') return;
      updateSuggestionsScrollY(cacheKey, scrollEl.scrollTop);
    };
    /* v8 ignore stop */
    scrollEl.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      onScroll();
      scrollEl.removeEventListener('scroll', onScroll);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cacheKey]);

  // Initialize generator once when source data is ready
  useEffect(() => {
    if (mode === 'solo' && !accountId) {
      if (!initializedRef.current) readyRef.current = false;
      return;
    }
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

    suggestionsCache = {
      cacheKey,
      categories: first,
      generator: gen,
      loadTriggerCount: 0,
    };
    initializeSuggestionsScrollState(cacheKey);
  // eslint-disable-next-line react-hooks/exhaustive-deps -- currentSeason/options only needed at init
  }, [accountId, cacheKey, coreSongs, scoresIndex, sourceReady]);

  // Fetch rival data and inject into the generator when it becomes ready.
  useEffect(() => {
    if (mode !== 'solo') return;
    if (!accountId) return;
    if (!generatorRef.current || rivalDataInjectedRef.current) return;

    const controller = new AbortController();
    const combo = deriveComboFromSettings(settings) ?? undefined;

    api.getRivalsAll(accountId, { signal: controller.signal })
      .then((response) => {
        if (controller.signal.aborted || !generatorRef.current) return;
        const index = buildRivalDataIndexFromRivalsAll(response, combo, 5);
        generatorRef.current.setRivalData(index);
        rivalDataInjectedRef.current = true;
      })
      .catch(() => {
        // Graceful degradation — rival suggestions simply won't appear
      });

    return () => controller.abort();
  // eslint-disable-next-line react-hooks/exhaustive-deps -- inject once after generator init using the current settings snapshot
  }, [accountId, mode, settings, generatorRef.current]);

  const loadMore = useCallback(() => {
    const gen = generatorRef.current;
    if (!gen || !readyRef.current) return;
    if (loadMorePendingRef.current) return;
    loadMorePendingRef.current = true;
    loadTriggerCountRef.current += 1;
    if (suggestionsCache?.cacheKey === cacheKey) {
      suggestionsCache.loadTriggerCount = loadTriggerCountRef.current;
    }

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
      if (suggestionsCache?.cacheKey === cacheKey) {
        suggestionsCache.categories = updated;
      }
      return updated;
    });
  }, [cacheKey]);

  useEffect(() => {
    loadMorePendingRef.current = false;
  }, [cacheKey, categories.length, hasMore]);

  const resetScrollPosition = useCallback(() => {
    updateSuggestionsScrollY(cacheKey, 0);
  }, [cacheKey]);

  return {
    categories,
    loadMore,
    hasMore,
    loadTriggerCount: loadTriggerCountRef.current,
    resetScrollPosition,
  };
}
