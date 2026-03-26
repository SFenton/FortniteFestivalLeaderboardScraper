import { useCallback, useRef, type CSSProperties } from 'react';
import { FADE_DURATION, STAGGER_INTERVAL } from '@festival/theme';

/**
 * Encapsulates the stagger animation pattern used across pages.
 *
 * Returns helpers to produce inline fadeInUp animation styles for sequential items.
 * When `shouldStagger` is false, all helpers return `undefined` (no animation).
 *
 * @param shouldStagger  Whether animations should play (false = return-visit, skip)
 * @param interval       Delay between items in ms (default: STAGGER_INTERVAL = 125)
 */
export function useStagger(shouldStagger: boolean, interval: number = STAGGER_INTERVAL) {
  const idxRef = useRef(0);

  // Reset counter on each render so sequential calls to next() start from 0
  idxRef.current = 0;

  /** Build an inline stagger style for a specific delay in ms. */
  const forDelay = useCallback((delayMs: number): CSSProperties | undefined => {
    if (!shouldStagger) return undefined;
    return {
      opacity: 0,
      animation: `fadeInUp ${FADE_DURATION}ms ease-out ${delayMs}ms forwards`,
    };
  }, [shouldStagger]);

  /** Build an inline stagger style for a specific index. */
  const forIndex = useCallback((index: number, offset = 0): CSSProperties | undefined => {
    if (!shouldStagger) return undefined;
    return {
      opacity: 0,
      animation: `fadeInUp ${FADE_DURATION}ms ease-out ${offset + index * interval}ms forwards`,
    };
  }, [shouldStagger, interval]);

  /** Auto-incrementing stagger — call once per item in render order. */
  const next = useCallback((offset = 0): CSSProperties | undefined => {
    if (!shouldStagger) return undefined;
    const idx = idxRef.current++;
    return {
      opacity: 0,
      animation: `fadeInUp ${FADE_DURATION}ms ease-out ${offset + idx * interval}ms forwards`,
    };
  }, [shouldStagger, interval]);

  /** Shared onAnimationEnd handler that cleans up inline styles. */
  const clearAnim = useCallback((e: React.AnimationEvent<HTMLElement>) => {
    const el = e.currentTarget;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);

  return { forDelay, forIndex, next, clearAnim };
}
