import { useRef } from 'react';
import { useLoadPhase, type LoadPhase } from '../data/useLoadPhase';

/**
 * Module-level set tracking which page cache keys have been rendered.
 * Survives across component mounts/unmounts within the same session.
 */
const visitedKeys = new Set<string>();

/**
 * Combines page visit tracking + load phase management into a single hook.
 *
 * Replaces the per-page pattern of:
 *   let _pageHasRendered = false;
 *   const skipAnimRef = useRef(_pageHasRendered && hasCachedData);
 *   _pageHasRendered = true;
 *   const { phase, shouldStagger } = useLoadPhase(isReady, { skipAnimation: skipAnimRef.current });
 *
 * On first visit (key not in visited set): full stagger animation.
 * On return visit (key already visited + cached data): skip animation.
 *
 * @param cacheKey   Unique key per page route (e.g. `rivals:${accountId}`, `settings`)
 * @param isReady    True when all async data has loaded
 * @param hasCachedData  True when module-level data cache exists for this key
 */
export function usePageTransition(
  cacheKey: string,
  isReady: boolean,
  hasCachedData = false,
): { phase: LoadPhase; shouldStagger: boolean } {
  // Determine skip at mount time — skip if we've visited this key before
  // and cached data exists. This covers back-navigation, layout remounts
  // (mobile↔desktop resize), and re-visits to the same page.
  const skipAnim = useRef(
    visitedKeys.has(cacheKey) && hasCachedData,
  ).current;

  // Mark as visited after the skip decision
  visitedKeys.add(cacheKey);

  return useLoadPhase(isReady, { skipAnimation: skipAnim });
}

/**
 * Clear the visited keys set.
 * Call when settings change or caches are globally invalidated.
 */
export function clearPageTransitionCache(key?: string): void {
  if (key) visitedKeys.delete(key);
  else visitedKeys.clear();
}

/**
 * Check whether a page cache key has been visited this session.
 * Use in pages with custom stagger logic that can't use `usePageTransition`
 * but still need to skip animations on layout remounts.
 */
export function hasVisitedPage(key: string): boolean {
  return visitedKeys.has(key);
}

/**
 * Mark a page cache key as visited this session.
 * Call after reading `hasVisitedPage` so the first render staggers
 * but subsequent mounts (layout remount, back-nav) skip.
 */
export function markPageVisited(key: string): void {
  visitedKeys.add(key);
}
