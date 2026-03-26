import { useRef } from 'react';
import { useNavigationType } from 'react-router-dom';
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
 *   const skipAnimRef = useRef(_pageHasRendered && navType === 'POP' && hasCachedData);
 *   _pageHasRendered = true;
 *   const { phase, shouldStagger } = useLoadPhase(isReady, { skipAnimation: skipAnimRef.current });
 *
 * On first visit (key not in visited set): full stagger animation.
 * On return visit (POP + key already visited): skip animation.
 * On PUSH to same key: treat as first visit (re-stagger).
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
  const navType = useNavigationType();

  // Determine skip at mount time — only skip if we've visited this key before,
  // navigation is POP (back), and cached data exists
  const skipAnim = useRef(
    visitedKeys.has(cacheKey) && navType === 'POP' && hasCachedData,
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
