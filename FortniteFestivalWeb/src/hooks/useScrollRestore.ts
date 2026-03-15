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
  navType: string,
): () => void {
  // Restore on mount for back-navigation
  useEffect(() => {
    if (navType !== 'POP') {
      // PUSH / REPLACE: reset stored position so next back-nav starts fresh
      scrollStore.delete(cacheKey);
      return;
    }
    const saved = scrollStore.get(cacheKey);
    if (saved == null || saved <= 0) return;
    const el = scrollRef.current;
    if (!el) return;

    // content-visibility: auto can delay element sizing.
    // Try immediately, then retry after a frame if scrollHeight is too small.
    const tryRestore = () => {
      const target = scrollRef.current;
      if (!target) return;
      if (target.scrollHeight >= saved) {
        target.scrollTop = saved;
      }
    };
    tryRestore();
    const raf = requestAnimationFrame(tryRestore);
    return () => cancelAnimationFrame(raf);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps -- mount-only

  const saveScroll = useCallback(() => {
    const el = scrollRef.current;
    if (el) scrollStore.set(cacheKey, el.scrollTop);
  }, [scrollRef, cacheKey]);

  return saveScroll;
}
