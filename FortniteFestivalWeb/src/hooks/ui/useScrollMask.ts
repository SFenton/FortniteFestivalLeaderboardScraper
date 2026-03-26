import { useCallback, useEffect, useRef, type RefObject } from 'react';

export interface ScrollMaskOptions {
  /** Fade zone size in pixels. Default: 40 */
  size?: number;
}

const DEFAULT_SIZE = 40;

/**
 * Applies a CSS `mask-image` on a container based on the browser's scroll
 * position relative to the container's bounds.
 *
 * Uses window scroll events (no per-element scroll container required).
 * Fades content at whichever edges have more content above/below the viewport.
 */
export function useScrollMask(
  containerRef: RefObject<HTMLElement | null>,
  deps: readonly unknown[] = [],
  options: ScrollMaskOptions = {},
): () => void {
  const size = options.size ?? DEFAULT_SIZE;
  const rafId = useRef(0);
  const lastState = useRef(-1);

  const update = useCallback(() => {
    const el = containerRef.current;
    if (!el) return;

    const rect = el.getBoundingClientRect();
    const atTop = rect.top >= 0;
    const atBottom = rect.bottom <= window.innerHeight + 1;

    const state = (atTop && atBottom) ? 0 : atTop ? 1 : atBottom ? 2 : 3;
    if (state === lastState.current) return;
    lastState.current = state;

    let mask: string;
    if (state === 0) {
      mask = '';
    } else if (state === 1) {
      mask = `linear-gradient(to bottom, black calc(100% - ${size}px), transparent)`;
    } else if (state === 2) {
      mask = `linear-gradient(to bottom, transparent, black ${size}px)`;
    } else {
      mask = `linear-gradient(to bottom, transparent, black ${size}px, black calc(100% - ${size}px), transparent)`;
    }

    el.style.maskImage = mask;
    el.style.webkitMaskImage = mask;
  }, [size, containerRef]);

  /** rAF-throttled wrapper — at most one update per animation frame. */
  const throttledUpdate = useCallback(() => {
    if (rafId.current) return;
    rafId.current = requestAnimationFrame(() => {
      rafId.current = 0;
      update();
    });
  }, [update]);

  // Cancel pending rAF on unmount
  useEffect(() => () => { cancelAnimationFrame(rafId.current); }, []);

  // Listen to window scroll
  useEffect(() => {
    window.addEventListener('scroll', throttledUpdate, { passive: true });
    return () => window.removeEventListener('scroll', throttledUpdate);
  }, [throttledUpdate]);

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { update(); }, [update, ...deps]);

  return throttledUpdate;
}
