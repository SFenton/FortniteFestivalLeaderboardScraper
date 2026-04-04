import { useState, useLayoutEffect, useRef, type RefObject } from 'react';

function measureWidth(el: HTMLElement): number {
  return Math.round(el.getBoundingClientRect().width || el.clientWidth || el.offsetWidth || 0);
}

/**
 * Track the inline content width of a container element via ResizeObserver.
 * Measures synchronously on mount so responsive layout decisions do not flash through a `0` width state.
 */
export function useContainerWidth(ref: RefObject<HTMLElement | null>): number {
  const [width, setWidth] = useState(0);
  const lastWidthRef = useRef(0);

  /* v8 ignore start — ResizeObserver callback requires real DOM */
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;

    let rafId = 0;
    const commitWidth = (nextWidth: number) => {
      const rounded = Math.round(nextWidth);
      if (rounded <= 0) return;
      if (Math.abs(rounded - lastWidthRef.current) < 2) return;

      lastWidthRef.current = rounded;
      setWidth(prev => (Math.abs(rounded - prev) < 2 ? prev : rounded));
    };

    commitWidth(measureWidth(el));

    const ro = new ResizeObserver((entries) => {
      const observedWidth = entries[0]?.contentRect.width ?? measureWidth(el);
      cancelAnimationFrame(rafId);
      rafId = requestAnimationFrame(() => commitWidth(observedWidth));
    });

    ro.observe(el);
    return () => {
      cancelAnimationFrame(rafId);
      ro.disconnect();
    };
  }, [ref]);
  /* v8 ignore stop */

  return width;
}
