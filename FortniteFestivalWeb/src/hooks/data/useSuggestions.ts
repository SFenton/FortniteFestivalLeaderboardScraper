import {
  startTransition,
  useState,
  useRef,
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
} from 'react';
import { useQuery } from '@tanstack/react-query';
import { SuggestionGenerator } from '@festival/core/suggestions';
import type { SuggestionCategory } from '@festival/core/types';
import type { Song as CoreSong, LeaderboardData } from '@festival/core/types';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';
import { buildRivalDataIndexFromRivalsAll } from '../../utils/suggestionAdapter';
import { deriveComboFromSettings } from '../../pages/rivals/helpers/comboUtils';
import { useSettings } from '../../contexts/SettingsContext';
import { rivalsAllQueryOptions } from '../../api/remoteDataQueries';
import { normalizeRoutePathname, Routes } from '../../routes';
import {
  initializeSuggestionsScrollState,
  updateSuggestionsScrollY,
} from '../../pages/suggestions/suggestionsSessionCache';

const BATCH_SIZE = 6;
const INITIAL_BATCH = 10;
export const SUGGESTIONS_CATEGORY_LIMIT = 1_000;

type SuggestionsMode = 'solo' | 'band';

type UseSuggestionsOptions = {
  mode?: SuggestionsMode;
  cacheKey?: string;
  sourceReady?: boolean;
  bandComboId?: string | null;
};

// Module-level cache so generated categories survive route navigation.
type SuggestionsCache = {
  sourceKey: string;
  mixKey: string;
  categories: SuggestionCategory[];
  generator: SuggestionGenerator;
  generatorHasMore: boolean;
  loadTriggerCount: number;
  rivalDataRevision: string | null;
};

type SuggestionsSession = {
  sourceKey: string;
  mixKey: string | null;
  categories: SuggestionCategory[];
  generatorHasMore: boolean;
  loadTriggerCount: number;
};

let suggestionsCache: SuggestionsCache | null = null;
let nextMixSequence = 0;

function toSession(cache: SuggestionsCache | null, sourceKey: string): SuggestionsSession {
  return cache
    ? {
        sourceKey: cache.sourceKey,
        mixKey: cache.mixKey,
        categories: cache.categories,
        generatorHasMore: cache.generatorHasMore,
        loadTriggerCount: cache.loadTriggerCount,
      }
    : {
        sourceKey,
        mixKey: null,
        categories: [],
        generatorHasMore: true,
        loadTriggerCount: 0,
      };
}

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
  const rivalCombo = useMemo(
    () => deriveComboFromSettings(settings) ?? undefined,
    [settings],
  );
  const rivalsAllQuery = useQuery({
    ...rivalsAllQueryOptions(accountId),
    enabled: mode === 'solo' && !!accountId,
  });
  const rivalData = useMemo(
    () => rivalsAllQuery.data
      ? buildRivalDataIndexFromRivalsAll(rivalsAllQuery.data, rivalCombo, 5)
      : null,
    [rivalCombo, rivalsAllQuery.data],
  );
  const rivalDataRevision = rivalData
    ? `${rivalsAllQuery.dataUpdatedAt}:${rivalCombo ?? ''}`
    : null;

  // Restore from cache if same suggestion identity
  const cached = suggestionsCache?.sourceKey === cacheKey
    ? suggestionsCache
    : null;

  const [session, setSession] = useState<SuggestionsSession>(
    () => toSession(cached, cacheKey),
  );
  const generatorRef = useRef<SuggestionGenerator | null>(cached?.generator ?? null);
  const mixKeyRef = useRef<string | null>(cached?.mixKey ?? null);
  const readyRef = useRef(!!cached);
  const initializedRef = useRef(!!cached);
  const cacheKeyRef = useRef(cacheKey);
  const lastInjectedRivalDataRevisionRef = useRef(cached?.rivalDataRevision ?? null);
  const loadMorePendingRef = useRef(false);
  const currentSession = session.sourceKey === cacheKey
    ? session
    : toSession(cached, cacheKey);

  useEffect(() => {
    if (cacheKeyRef.current === cacheKey) return;

    cacheKeyRef.current = cacheKey;
    const nextCached = suggestionsCache?.sourceKey === cacheKey
      ? suggestionsCache
      : null;
    generatorRef.current = nextCached?.generator ?? null;
    mixKeyRef.current = nextCached?.mixKey ?? null;
    readyRef.current = !!nextCached;
    initializedRef.current = !!nextCached;
    lastInjectedRivalDataRevisionRef.current = nextCached?.rivalDataRevision ?? null;
    loadMorePendingRef.current = false;
    setSession(toSession(nextCached, cacheKey));
  }, [cacheKey]);

  // Continuously save scroll position so browser back works
  const scrollContainerRef = useScrollContainer();
  useLayoutEffect(() => {
    const scrollCacheKey = currentSession.mixKey;
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl || !scrollCacheKey) return;
    /* v8 ignore start — scroll position tracking */
    const onScroll = () => {
      const hashPath = window.location.hash.slice(1).split('?')[0] ?? Routes.root;
      if (normalizeRoutePathname(hashPath) !== Routes.suggestions) return;
      updateSuggestionsScrollY(scrollCacheKey, scrollEl.scrollTop);
    };
    /* v8 ignore stop */
    scrollEl.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      onScroll();
      scrollEl.removeEventListener('scroll', onScroll);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentSession.mixKey]);

  const createMix = useCallback((): SuggestionsCache => {
    nextMixSequence += 1;
    const mixKey = `${cacheKey}:mix:${nextMixSequence}`;
    const gen = new SuggestionGenerator({
      seed: Date.now() + nextMixSequence,
      currentSeason,
      mode,
      bandComboId: options.bandComboId,
    });
    gen.setSource(coreSongs, scoresIndex);
    if (rivalData) gen.setRivalData(rivalData);

    return {
      sourceKey: cacheKey,
      mixKey,
      categories: gen.getNext(Math.min(INITIAL_BATCH, SUGGESTIONS_CATEGORY_LIMIT)),
      generator: gen,
      generatorHasMore: true,
      loadTriggerCount: 0,
      rivalDataRevision,
    };
  }, [
    cacheKey,
    coreSongs,
    currentSeason,
    mode,
    options.bandComboId,
    rivalData,
    rivalDataRevision,
    scoresIndex,
  ]);

  const installFreshMix = useCallback((nextCache: SuggestionsCache) => {
    suggestionsCache = nextCache;
    cacheKeyRef.current = nextCache.sourceKey;
    generatorRef.current = nextCache.generator;
    mixKeyRef.current = nextCache.mixKey;
    readyRef.current = true;
    initializedRef.current = true;
    lastInjectedRivalDataRevisionRef.current = nextCache.rivalDataRevision;
    loadMorePendingRef.current = false;
    initializeSuggestionsScrollState(nextCache.mixKey);
    setSession(toSession(nextCache, nextCache.sourceKey));
  }, []);

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
    installFreshMix(createMix());
  // eslint-disable-next-line react-hooks/exhaustive-deps -- currentSeason/options only needed at init
  }, [accountId, cacheKey, coreSongs, createMix, installFreshMix, scoresIndex, sourceReady]);

  // Inject newly available query-owned rival data without replacing the mix.
  useEffect(() => {
    const generator = generatorRef.current;
    if (
      !generator
      || !currentSession.mixKey
      || !rivalData
      || !rivalDataRevision
      || lastInjectedRivalDataRevisionRef.current === rivalDataRevision
    ) return;

    generator.setRivalData(rivalData);
    lastInjectedRivalDataRevisionRef.current = rivalDataRevision;
    const activeCache = suggestionsCache;
    if (
      activeCache?.sourceKey === cacheKey
      && activeCache.mixKey === currentSession.mixKey
      && activeCache.generator === generator
    ) {
      activeCache.rivalDataRevision = rivalDataRevision;
      if (!activeCache.generatorHasMore) {
        activeCache.generatorHasMore = true;
        startTransition(() => {
          setSession(toSession(activeCache, cacheKey));
        });
      }
    }
  }, [cacheKey, currentSession.mixKey, rivalData, rivalDataRevision]);

  const loadMore = useCallback(() => {
    const gen = generatorRef.current;
    const mixKey = mixKeyRef.current;
    const activeCache = suggestionsCache;
    if (
      !gen
      || !mixKey
      || !readyRef.current
      || cacheKeyRef.current !== cacheKey
      || activeCache?.sourceKey !== cacheKey
      || activeCache.mixKey !== mixKey
      || activeCache.generator !== gen
      || !activeCache.generatorHasMore
    ) return;
    if (loadMorePendingRef.current) return;
    const remaining = SUGGESTIONS_CATEGORY_LIMIT - activeCache.categories.length;
    if (remaining <= 0) return;

    loadMorePendingRef.current = true;
    const loadTriggerCount = activeCache.loadTriggerCount + 1;
    const requestedCount = Math.min(BATCH_SIZE, remaining);

    let next = gen.getNext(requestedCount);
    if (next.length === 0) {
      gen.resetForEndless();
      next = gen.getNext(requestedCount);
    }
    if (
      suggestionsCache !== activeCache
      || mixKeyRef.current !== mixKey
      || generatorRef.current !== gen
    ) return;

    if (next.length === 0) {
      activeCache.generatorHasMore = false;
      activeCache.loadTriggerCount = loadTriggerCount;
      startTransition(() => {
        setSession(toSession(activeCache, cacheKey));
      });
      return;
    }

    activeCache.categories = [
      ...activeCache.categories,
      ...next.slice(0, remaining),
    ];
    activeCache.loadTriggerCount = loadTriggerCount;
    startTransition(() => {
      setSession(toSession(activeCache, cacheKey));
    });
  }, [cacheKey]);

  useEffect(() => {
    loadMorePendingRef.current = false;
  }, [
    cacheKey,
    currentSession.categories.length,
    currentSession.generatorHasMore,
    currentSession.mixKey,
  ]);

  const startNewMix = useCallback(() => {
    if (
      (mode === 'solo' && !accountId)
      || !sourceReady
      || coreSongs.length === 0
    ) return;

    const nextCache = createMix();
    installFreshMix(nextCache);
    scrollContainerRef.current?.scrollTo(0, 0);
  }, [
    accountId,
    coreSongs.length,
    createMix,
    installFreshMix,
    mode,
    scrollContainerRef,
    sourceReady,
  ]);

  const resetScrollPosition = useCallback(() => {
    const mixKey = mixKeyRef.current;
    if (mixKey) updateSuggestionsScrollY(mixKey, 0);
  }, []);

  const limitReached = currentSession.categories.length >= SUGGESTIONS_CATEGORY_LIMIT;
  const hasMore = currentSession.generatorHasMore && !limitReached;

  return {
    categories: currentSession.categories,
    mixKey: currentSession.mixKey,
    loadMore,
    hasMore,
    limitReached,
    loadTriggerCount: currentSession.loadTriggerCount,
    startNewMix,
    resetScrollPosition,
  };
}
