import { useEffect, useCallback, type RefObject } from 'react';

/**
 * Module-level store keyed by `cacheKey`.
 * Survives component unmounts; cleared via `clearScrollCache`.
 */
const scrollStore = new Map<string, number>();

/**
 * Clear one or all cached scroll positions.
 * Call when settings change or caches are invalidated.
 */
export function clearScrollCache(key?: string): void {
  if (key) scrollStore.delete(key);
  else scrollStore.clear();
}

/**
 * Saves scroll position on every scroll and restores it on mount when
 * the user navigated back (POP).
 *
 * @param scrollRef   Ref to the scrollable container
 * @param cacheKey    Unique key for this page / route (e.g. 'songs', `songDetail:${id}`)
 * @param navType     React Router navigation type ('POP' | 'PUSH' | 'REPLACE')
 * @returns `saveScroll` — call from the container's `onScroll` handler
 */
export function useScrollRestore(
  scrollRef: RefObject<HTMLElement | null>,
  cacheKey: string,
  _navType: string,
): () => void {
  // Restore scroll position on mount when revisiting a page.
  // The saveScroll callback persists position on every scroll, so if a cached
  // value exists it means the user was here before — restore it regardless of
  // whether this is a POP (back) or PUSH (tab switch).
  useEffect(() => {
    const saved = scrollStore.get(cacheKey);
    if (saved == null || saved <= 0) return;
    const el = scrollRef.current;
    /* v8 ignore start */
    if (!el) return;
    /* v8 ignore stop */

    // content-visibility: auto can delay element sizing.
    // Try immediately, then retry after a frame if scrollHeight is too small.
    /* v8 ignore start -- scrollTop/rAF: DOM scroll APIs not available in jsdom */
    const tryRestore = () => {
      const target = scrollRef.current;
      /* v8 ignore start */
      if (!target) return;
      /* v8 ignore stop */
      if (target.scrollHeight >= saved) {
        target.scrollTop = saved;
      }
    };
    tryRestore();
    const raf = requestAnimationFrame(tryRestore);
    return () => cancelAnimationFrame(raf);
    /* v8 ignore stop */
  }, []); // eslint-disable-line react-hooks/exhaustive-deps -- mount-only

  const saveScroll = useCallback(() => {
    const el = scrollRef.current;
    /* v8 ignore next -- scrollTop: DOM API */
    if (el) scrollStore.set(cacheKey, el.scrollTop);
  }, [scrollRef, cacheKey]);

  return saveScroll;
}
