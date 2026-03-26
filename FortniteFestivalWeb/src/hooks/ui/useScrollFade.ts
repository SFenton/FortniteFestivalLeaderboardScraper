import { useCallback, useEffect, useRef, type RefObject } from 'react';

export interface ScrollFadeOptions {
  /** How many pixels the fade zone extends inward from each scroll edge. Default: 36 */
  distance?: number;
  /**
   * Opacity ramp from 0→1 as an array of [position, opacity] tuples.
   * Position is 0 (clip edge) to 1 (fully opaque). Default: exponential curve.
   */
  stops?: ReadonlyArray<readonly [number, number]>;
}

const DEFAULT_DISTANCE = 36;

/* eslint-disable no-magic-numbers -- opacity ramp curve: [position, opacity] tuples */
const DEFAULT_STOPS: ReadonlyArray<readonly [number, number]> = [
  [0, 0], [0.15, 0.01], [0.30, 0.03], [0.45, 0.08],
  [0.60, 0.18], [0.75, 0.40], [0.90, 0.70], [1, 1],
];
/* eslint-enable no-magic-numbers */

/**
 * Applies per-child mask-image fading at the viewport edges using
 * IntersectionObserver for efficient tracking. Only children near the
 * edges get `getBoundingClientRect` calls — typically 0–2 at any time.
 *
 * @param scrollRef  Ref to the scrollable container (or viewport parent)
 * @param listRef    Ref to the direct parent of the items to fade
 * @param deps       Extra dependency array — when any value changes, observers reconnect
 * @param options    Fade distance and curve configuration
 * @returns          `update` handler (called automatically on window scroll)
 */
export function useScrollFade(
  scrollRef: RefObject<HTMLElement | null>,
  listRef: RefObject<HTMLElement | null>,
  deps: readonly unknown[] = [],
  options: ScrollFadeOptions = {},
): () => void {
  const distance = options.distance ?? DEFAULT_DISTANCE;
  const stops = options.stops ?? DEFAULT_STOPS;
  const rafId = useRef(0);

  // Track which children are near viewport edges
  const edgeChildrenRef = useRef(new Set<HTMLElement>());

  const applyMasks = useCallback(() => {
    const listEl = listRef.current;
    if (!listEl) return;

    const vh = window.innerHeight;
    const atTop = window.scrollY <= 0;
    const atBottom = window.scrollY + vh >= document.documentElement.scrollHeight - 1;
    const topFadeDistance = atTop ? 0 : Math.min(window.scrollY, distance);

    // Only process children that IntersectionObserver flagged as near edges
    for (const child of edgeChildrenRef.current) {
      const rect = child.getBoundingClientRect();

      const needsTop = !atTop && rect.top < topFadeDistance;
      const needsBottom = !atBottom && rect.bottom > vh - distance;

      if (!needsTop && !needsBottom) {
        child.style.maskImage = '';
        child.style.webkitMaskImage = '';
      } else if (needsTop && needsBottom) {
        const clipTop = -rect.top;
        const clipBottom = vh - rect.top;
        const topStops = stops.map(([t, a]) =>
          `rgba(0,0,0,${a}) ${clipTop + t * topFadeDistance}px`
        );
        const bottomStops = stops.slice().reverse().map(([t, a]) =>
          `rgba(0,0,0,${a}) ${clipBottom - t * distance}px`
        );
        const mask = `linear-gradient(to bottom, ${topStops.join(', ')}, black ${clipTop + topFadeDistance}px, black ${clipBottom - distance}px, ${bottomStops.join(', ')})`;
        child.style.maskImage = mask;
        child.style.webkitMaskImage = mask;
      } else if (needsTop) {
        const clipTop = -rect.top;
        const s = stops.map(([t, a]) =>
          `rgba(0,0,0,${a}) ${clipTop + t * topFadeDistance}px`
        ).join(', ');
        const mask = `linear-gradient(to bottom, ${s})`;
        child.style.maskImage = mask;
        child.style.webkitMaskImage = mask;
      } else {
        const clipBottom = vh - rect.top;
        const s = stops.slice().reverse().map(([t, a]) =>
          `rgba(0,0,0,${a}) ${clipBottom - t * distance}px`
        ).join(', ');
        const mask = `linear-gradient(to bottom, ${s})`;
        child.style.maskImage = mask;
        child.style.webkitMaskImage = mask;
      }
    }

    // Clear masks on children that left the edge zone
    for (let i = 0; i < listEl.children.length; i++) {
      const child = listEl.children[i] as HTMLElement;
      if (!edgeChildrenRef.current.has(child)) {
        if (child.style.maskImage) {
          child.style.maskImage = '';
          child.style.webkitMaskImage = '';
        }
      }
    }
  }, [distance, stops, listRef]);

  const throttledUpdate = useCallback(() => {
    if (rafId.current) return;
    rafId.current = requestAnimationFrame(() => {
      rafId.current = 0;
      applyMasks();
    });
  }, [applyMasks]);

  // Set up IntersectionObserver to track children near viewport edges
  useEffect(() => {
    const listEl = listRef.current;
    if (!listEl) return;

    // Observe with margin that extends the "near edge" zone
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          const el = entry.target as HTMLElement;
          if (entry.isIntersecting) {
            edgeChildrenRef.current.add(el);
          } else {
            edgeChildrenRef.current.delete(el);
            // Clear mask when fully out of view
            if (el.style.maskImage) {
              el.style.maskImage = '';
              el.style.webkitMaskImage = '';
            }
          }
        }
        throttledUpdate();
      },
      {
        // rootMargin extends the observation zone so items entering the fade distance are caught early
        rootMargin: `${distance}px 0px ${distance}px 0px`,
        threshold: [0, 0.1, 0.9, 1],
      },
    );

    for (let i = 0; i < listEl.children.length; i++) {
      observer.observe(listEl.children[i]);
    }

    return () => observer.disconnect();
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [distance, throttledUpdate, ...deps]);

  // Listen to window scroll for mask updates on tracked elements
  useEffect(() => {
    window.addEventListener('scroll', throttledUpdate, { passive: true });
    return () => window.removeEventListener('scroll', throttledUpdate);
  }, [throttledUpdate]);

  useEffect(() => () => { cancelAnimationFrame(rafId.current); }, []);

  // Initial computation
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { applyMasks(); }, [applyMasks, ...deps]);

  return throttledUpdate;
}
