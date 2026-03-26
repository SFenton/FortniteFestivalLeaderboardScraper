import { useEffect, useCallback } from 'react';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';

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
 * Uses the app's scroll container element (via ScrollContainerContext).
 *
 * @param cacheKey    Unique key for this page / route (e.g. 'songs', `songDetail:${id}`)
 * @param _navType    React Router navigation type (kept for API compat, not used)
 * @returns `saveScroll` — call from a scroll handler or attach to container
 */
export function useScrollRestore(
  cacheKey: string,
  _navType: string,
): () => void {
  const scrollContainerRef = useScrollContainer();

  // Restore scroll position on mount when revisiting a page.
  useEffect(() => {
    const saved = scrollStore.get(cacheKey);
    if (saved == null || saved <= 0) return;
    const container = scrollContainerRef.current;
    if (!container) return;

    const tryRestore = () => {
      if (container.scrollHeight >= saved) {
        container.scrollTo(0, saved);
      }
    };
    tryRestore();
    const raf = requestAnimationFrame(tryRestore);
    return () => cancelAnimationFrame(raf);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps -- mount-only

  // Persist scroll position on every scroll.
  useEffect(() => {
    const container = scrollContainerRef.current;
    if (!container) return;
    const handler = () => { scrollStore.set(cacheKey, container.scrollTop); };
    container.addEventListener('scroll', handler, { passive: true });
    return () => container.removeEventListener('scroll', handler);
  }, [cacheKey, scrollContainerRef]);

  // Return a manual save function for imperative use.
  const saveScroll = useCallback(() => {
    scrollStore.set(cacheKey, scrollContainerRef.current?.scrollTop ?? 0);
  }, [cacheKey, scrollContainerRef]);

  return saveScroll;
}
