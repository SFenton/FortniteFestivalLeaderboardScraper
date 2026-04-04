import { useState, useEffect, type RefObject } from 'react';

/**
 * Track the inline content width of a container element via ResizeObserver.
 * Returns 0 until the first measurement.
 */
export function useContainerWidth(ref: RefObject<HTMLElement | null>): number {
  const [width, setWidth] = useState(0);
  /* v8 ignore start — ResizeObserver callback requires real DOM */
  useEffect(() => {
    const el = ref.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width ?? 0;
      if (w > 0) setWidth(w);
    });
    ro.observe(el);
    return () => ro.disconnect();
  }, [ref]);
  /* v8 ignore stop */
  return width;
}
