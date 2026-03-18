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

const DEFAULT_STOPS: ReadonlyArray<readonly [number, number]> = [
  [0, 0], [0.15, 0.01], [0.30, 0.03], [0.45, 0.08],
  [0.60, 0.18], [0.75, 0.40], [0.90, 0.70], [1, 1],
];

function buildTopMask(clipTop: number, distance: number, stops: ReadonlyArray<readonly [number, number]>): string {
  const s = stops.map(([t, a]) =>
    `rgba(0,0,0,${a}) ${clipTop + t * distance}px`
  ).join(', ');
  return `linear-gradient(to bottom, ${s})`;
}

function buildBottomMask(clipBottom: number, distance: number, stops: ReadonlyArray<readonly [number, number]>): string {
  const s = stops.slice().reverse().map(([t, a]) =>
    `rgba(0,0,0,${a}) ${clipBottom - t * distance}px`
  ).join(', ');
  return `linear-gradient(to bottom, ${s})`;
}

/**
 * Applies per-child mask-image fading on a scrollable container's children.
 * Cards fade to transparent at the scroll edges using an exponential ramp,
 * preserving `backdrop-filter` on each child element.
 *
 * @param scrollRef  Ref to the scrollable container (overflow-y: auto)
 * @param listRef    Ref to the direct parent of the items to fade
 * @param deps       Extra dependency array — when any value changes, fades are recomputed
 * @param options    Fade distance and curve configuration
 * @returns          `onScroll` handler to attach to the scroll container
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

  const update = useCallback(() => {
    const scrollEl = scrollRef.current;
    const listEl = listRef.current;
    if (!scrollEl || !listEl) return;
    const scrollRect = scrollEl.getBoundingClientRect();
    const atTop = scrollEl.scrollTop <= 0;
    const atBottom = scrollEl.scrollTop + scrollEl.clientHeight >= scrollEl.scrollHeight - 1;
    // Gradually ramp up the top fade over the first `distance` pixels of scroll
    const topFadeDistance = atTop ? 0 : Math.min(scrollEl.scrollTop, distance);

    for (let i = 0; i < listEl.children.length; i++) {
      const child = listEl.children[i] as HTMLElement;
      const rect = child.getBoundingClientRect();

      const clipTop = scrollRect.top - rect.top;
      const clipBottom = scrollRect.bottom - rect.top;

      const needsTop = !atTop && rect.top < scrollRect.top + topFadeDistance;
      const needsBottom = !atBottom && rect.bottom > scrollRect.bottom - distance;

      if (!needsTop && !needsBottom) {
        child.style.maskImage = '';
        child.style.webkitMaskImage = '';
      } else if (needsTop && needsBottom) {
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
        const mask = buildTopMask(clipTop, topFadeDistance, stops);
        child.style.maskImage = mask;
        child.style.webkitMaskImage = mask;
      } else {
        const mask = buildBottomMask(clipBottom, distance, stops);
        child.style.maskImage = mask;
        child.style.webkitMaskImage = mask;
      }
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [distance, stops]);

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

  // Recompute whenever deps change
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { update(); }, [update, ...deps]);

  return throttledUpdate;
}
