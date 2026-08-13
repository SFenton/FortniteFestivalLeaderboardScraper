type SuggestionsScrollState = {
  cacheKey: string;
  scrollY: number;
  restoreY: number;
  restorable: boolean;
};

const SCROLL_STATE_KEY = '__fstSuggestionsScrollState';
type SuggestionsScrollStore = {
  activeCacheKey: string | null;
  byCacheKey: Map<string, SuggestionsScrollState>;
};
type SuggestionsScrollGlobal = typeof globalThis & {
  __fstSuggestionsScrollState?: SuggestionsScrollStore;
};

function getSuggestionsScrollStore(): SuggestionsScrollStore {
  const global = globalThis as SuggestionsScrollGlobal;
  if (!global[SCROLL_STATE_KEY]) {
    Object.defineProperty(global, SCROLL_STATE_KEY, {
      configurable: true,
      value: {
        activeCacheKey: null,
        byCacheKey: new Map<string, SuggestionsScrollState>(),
      },
      writable: true,
    });
  }
  return global[SCROLL_STATE_KEY]!;
}

export function initializeSuggestionsScrollState(cacheKey: string): void {
  const store = getSuggestionsScrollStore();
  store.activeCacheKey = cacheKey;
  store.byCacheKey.set(cacheKey, {
    cacheKey,
    scrollY: 0,
    restoreY: 0,
    restorable: false,
  });
}

export function updateSuggestionsScrollY(cacheKey: string, scrollY: number): void {
  const store = getSuggestionsScrollStore();
  const state = store.byCacheKey.get(cacheKey);
  if (state) {
    store.activeCacheKey = cacheKey;
    state.scrollY = scrollY;
    if (!state.restorable) {
      state.restoreY = scrollY;
    }
  }
}

export function completeSuggestionsScrollRestoration(cacheKey: string, scrollY: number): void {
  const store = getSuggestionsScrollStore();
  const state = store.byCacheKey.get(cacheKey);
  if (state) {
    store.activeCacheKey = cacheKey;
    state.scrollY = scrollY;
    state.restoreY = scrollY;
    state.restorable = false;
  }
}

export function getSuggestionsScrollRestoreState(cacheKey: string | null): {
  matches: boolean;
  restorable: boolean;
  scrollY: number;
} {
  const cache = cacheKey
    ? getSuggestionsScrollStore().byCacheKey.get(cacheKey)
    : undefined;
  return cache
    ? {
        matches: true,
        restorable: cache.restorable,
        scrollY: cache.restorable ? cache.restoreY : cache.scrollY,
      }
    : {
        matches: false,
        restorable: false,
        scrollY: 0,
      };
}

export function markCurrentSuggestionsScrollRestorable(): void {
  const store = getSuggestionsScrollStore();
  const state = store.activeCacheKey
    ? store.byCacheKey.get(store.activeCacheKey)
    : undefined;
  if (state) {
    state.restoreY = state.scrollY;
    state.restorable = true;
  }
}
