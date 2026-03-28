import { useState, useCallback, useRef } from 'react';

/**
 * Returns `[columnCount, callbackRef]`.
 *
 * Attach `callbackRef` to a CSS grid container via `<div ref={callbackRef}>`.
 * The column count is measured synchronously when the element mounts (before
 * paint) and kept up-to-date via a `ResizeObserver`.
 *
 * Uses a callback ref so it works correctly with conditionally-rendered
 * elements (e.g. gated behind a loading phase).
 */
export function useGridColumnCount(): [number, (el: HTMLElement | null) => void] {
  const [cols, setCols] = useState(1);
  const roRef = useRef<ResizeObserver | null>(null);

  const ref = useCallback((el: HTMLElement | null) => {
    if (roRef.current) {
      roRef.current.disconnect();
      roRef.current = null;
    }
    if (!el) return;

    const measure = () => {
      const tracks = getComputedStyle(el).gridTemplateColumns;
      setCols(tracks ? tracks.split(' ').length : 1);
    };
    measure();

    roRef.current = new ResizeObserver(measure);
    roRef.current.observe(el);
  }, []);

  return [cols, ref];
}
