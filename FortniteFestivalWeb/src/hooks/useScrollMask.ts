import { useCallback, useEffect, useRef, type RefObject } from 'react';

export interface ScrollMaskOptions {
  /** Fade zone size in pixels. Default: 40 */
  size?: number;
}

const DEFAULT_SIZE = 40;

/**
 * Applies a CSS `mask-image` on a scrollable container so content fades to
 * transparent at whichever edges have more content to scroll.
 *
 * Only works when children do NOT use `backdrop-filter` — if they do, the
 * compositing layer created by the mask prevents the blur from reaching
 * content behind the container.  Use with `frostedCard` styles instead.
 *
 * One DOM write per scroll event on the container itself.
 */
export function useScrollMask(
  scrollRef: RefObject<HTMLElement | null>,
  deps: readonly unknown[] = [],
  options: ScrollMaskOptions = {},
): () => void {
  const size = options.size ?? DEFAULT_SIZE;
  const rafId = useRef(0);
  // Track scroll state to avoid redundant DOM writes: 0=both, 1=top, 2=bottom, 3=middle
  const lastState = useRef(-1);

  const update = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;

    const { scrollTop, scrollHeight, clientHeight } = el;
    const atTop = scrollTop <= 0;
    const atBottom = scrollTop + clientHeight >= scrollHeight - 1;

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
  }, [size, scrollRef]);

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

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { update(); }, [update, ...deps]);

  return throttledUpdate;
}
