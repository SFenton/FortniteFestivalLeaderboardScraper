import { useCallback, useEffect, useRef, type RefObject } from 'react';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';

export interface ScrollMaskOptions {
  /** Fade zone size in pixels. Default: 40 */
  size?: number;
}

const DEFAULT_SIZE = 40;

/**
 * Applies a CSS `mask-image` on a container based on the scroll container's
 * scroll position relative to the container's bounds.
 */
export function useScrollMask(
  containerRef: RefObject<HTMLElement | null>,
  deps: readonly unknown[] = [],
  options: ScrollMaskOptions = {},
): () => void {
  const size = options.size ?? DEFAULT_SIZE;
  const rafId = useRef(0);
  const hasMask = useRef(false);
  const scrollContainerRef = useScrollContainer();

  const update = useCallback(() => {
    const el = containerRef.current;
    const scrollEl = scrollContainerRef.current;
    if (!el || !scrollEl) return;

    const scrollRect = scrollEl.getBoundingClientRect();
    const rect = el.getBoundingClientRect();
    const atTop = rect.top >= scrollRect.top;
    const atBottom = rect.bottom <= scrollRect.bottom + 1;

    if (atTop && atBottom) {
      if (hasMask.current) {
        hasMask.current = false;
        el.style.maskImage = '';
        el.style.webkitMaskImage = '';
      }
      return;
    }

    // Positions within element coordinate space where viewport edges sit
    const topEdge = scrollRect.top - rect.top;
    const bottomEdge = scrollRect.bottom - rect.top;

    let mask: string;
    if (atTop) {
      mask = `linear-gradient(to bottom, black ${bottomEdge - size}px, transparent ${bottomEdge}px)`;
    } else if (atBottom) {
      mask = `linear-gradient(to bottom, transparent ${topEdge}px, black ${topEdge + size}px)`;
    } else {
      mask = `linear-gradient(to bottom, transparent ${topEdge}px, black ${topEdge + size}px, black ${bottomEdge - size}px, transparent ${bottomEdge}px)`;
    }

    hasMask.current = true;
    el.style.maskImage = mask;
    el.style.webkitMaskImage = mask;
  }, [size, containerRef, scrollContainerRef]);

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

  // Listen to scroll container
  useEffect(() => {
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl) return;
    scrollEl.addEventListener('scroll', throttledUpdate, { passive: true });
    return () => scrollEl.removeEventListener('scroll', throttledUpdate);
  }, [throttledUpdate, scrollContainerRef]);

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { update(); }, [update, ...deps]);

  return throttledUpdate;
}
