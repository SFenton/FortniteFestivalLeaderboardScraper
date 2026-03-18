/**
 * Hook that returns a stagger animation style and cleanup handler.
 * Replaces the repeated inline pattern:
 *   { opacity: 0, animation: `fadeInUp Xms ease-out ${delay}ms forwards` }
 *   onAnimationEnd: clear inline styles
 *
 * Usage:
 *   const { style, onAnimationEnd } = useStaggerStyle(delayMs, { skip });
 *   <div style={style} onAnimationEnd={onAnimationEnd}>...</div>
 */
import { useCallback, useMemo, type CSSProperties } from 'react';
import { FADE_DURATION } from '@festival/theme';

export interface StaggerStyleOptions {
  /** Skip animation entirely (render visible immediately). */
  skip?: boolean;
  /** Animation duration in ms. Default: FADE_DURATION (400). */
  duration?: number;
  /** Animation name. Default: 'fadeInUp'. */
  animation?: string;
}

export function useStaggerStyle(
  delayMs: number | null | undefined,
  options: StaggerStyleOptions = {},
): { style: CSSProperties | undefined; onAnimationEnd: (e: React.AnimationEvent) => void } {
  const { skip = false, duration = FADE_DURATION, animation = 'fadeInUp' } = options;

  const style: CSSProperties | undefined = useMemo(() => {
    if (skip || delayMs == null) return undefined;
    return {
      opacity: 0,
      animation: `${animation} ${duration}ms ease-out ${delayMs}ms forwards`,
    };
  }, [skip, delayMs, duration, animation]);

  const onAnimationEnd = useCallback((e: React.AnimationEvent) => {
    const el = e.currentTarget as HTMLElement;
    el.style.opacity = '';
    el.style.animation = '';
  }, []);

  return useMemo(() => ({ style, onAnimationEnd }), [style, onAnimationEnd]);
}

/**
 * Pure function to compute stagger delay for index-based lists.
 *
 * @param index   Item index in the list
 * @param offset  Base offset before the first item's delay (ms)
 * @param step    Delay increment per item (ms)
 * @param base    Optional base delay added to everything (e.g. parent card's delay)
 */
export function staggerMs(index: number, step: number, offset = 0, base = 0): number {
  return base + offset + index * step;
}

/**
 * Build stagger animation style for use inside .map() loops (non-hook).
 * Returns undefined when delay is null (skip animation).
 */
export function buildStaggerStyle(
  delayMs: number | null | undefined,
  opts: { duration?: number; animation?: string } = {},
): CSSProperties | undefined {
  if (delayMs == null) return undefined;
  const { duration = FADE_DURATION, animation = 'fadeInUp' } = opts;
  return { opacity: 0, animation: `${animation} ${duration}ms ease-out ${delayMs}ms forwards` };
}

/** Animation-end handler that clears inline opacity/animation styles. */
export function clearStaggerStyle(e: React.AnimationEvent): void {
  const el = e.currentTarget as HTMLElement;
  el.style.opacity = '';
  el.style.animation = '';
}
