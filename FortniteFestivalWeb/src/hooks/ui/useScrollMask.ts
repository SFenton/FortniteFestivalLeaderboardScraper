import { useCallback, useEffect, useRef, type RefObject } from 'react';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';

export interface ScrollMaskOptions {
  /** Fade zone size in pixels. Default: 40 */
  size?: number;
  /** When true, use the container's own scroll state instead of the app scroll container. Use for portalled overlays (modals). */
  selfScroll?: boolean;
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
  const selfScroll = options.selfScroll ?? false;
  const rafId = useRef(0);
  const hasMask = useRef(false);
  const scrollContainerRef = useScrollContainer();

  const update = useCallback(() => {
    const el = containerRef.current;

    let atTop: boolean;
    let atBottom: boolean;
    let topEdge: number;
    let bottomEdge: number;

    if (selfScroll) {
      if (!el) return;
      atTop = el.scrollTop <= 0;
      atBottom = el.scrollTop + el.clientHeight >= el.scrollHeight - 1;
      topEdge = 0;
      bottomEdge = el.clientHeight;
    } else {
      const scrollEl = scrollContainerRef.current;
      if (!el || !scrollEl) return;
      const scrollRect = scrollEl.getBoundingClientRect();
      const rect = el.getBoundingClientRect();
      atTop = rect.top >= scrollRect.top;
      atBottom = rect.bottom <= scrollRect.bottom + 1;
      topEdge = scrollRect.top - rect.top;
      bottomEdge = scrollRect.bottom - rect.top;
    }

    if (atTop && atBottom) {
      if (hasMask.current) {
        hasMask.current = false;
        el!.style.maskImage = '';
        el!.style.webkitMaskImage = '';
      }
      return;
    }

    let mask: string;
    if (atTop) {
      mask = `linear-gradient(to bottom, black ${bottomEdge - size}px, transparent ${bottomEdge}px)`;
    } else if (atBottom) {
      mask = `linear-gradient(to bottom, transparent ${topEdge}px, black ${topEdge + size}px)`;
    } else {
      mask = `linear-gradient(to bottom, transparent ${topEdge}px, black ${topEdge + size}px, black ${bottomEdge - size}px, transparent ${bottomEdge}px)`;
    }

    hasMask.current = true;
    el!.style.maskImage = mask;
    el!.style.webkitMaskImage = mask;
  }, [size, selfScroll, containerRef, scrollContainerRef]);

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

  // Listen to scroll container (self or app-level)
  useEffect(() => {
    const target = selfScroll ? containerRef.current : scrollContainerRef.current;
    if (!target) return;
    target.addEventListener('scroll', throttledUpdate, { passive: true });
    return () => target.removeEventListener('scroll', throttledUpdate);
  }, [selfScroll, throttledUpdate, containerRef, scrollContainerRef]);

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { update(); }, [update, ...deps]);

  return throttledUpdate;
}
