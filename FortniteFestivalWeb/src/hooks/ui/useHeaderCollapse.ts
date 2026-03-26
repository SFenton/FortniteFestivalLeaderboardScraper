import { useState, useCallback, useEffect } from 'react';

const DEFAULT_THRESHOLD = 40;

/**
 * Tracks whether a page header should be collapsed based on the browser's
 * scroll position (window.scrollY).
 *
 * @param opts.threshold  Pixel distance to trigger collapse (default 40)
 * @param opts.disabled   When true, returns the `forcedValue` without reading scroll
 * @param opts.forcedValue Value to use when disabled (default `false`)
 * @returns `[collapsed, onScroll]` — `onScroll` is provided for manual trigger but
 *          the hook also listens to window scroll automatically.
 */
export function useHeaderCollapse(
  opts?: { threshold?: number; disabled?: boolean; forcedValue?: boolean },
): [boolean, () => void] {
  const threshold = opts?.threshold ?? DEFAULT_THRESHOLD;
  const disabled = opts?.disabled ?? false;
  const forcedValue = opts?.forcedValue ?? false;

  const [collapsed, setCollapsed] = useState(disabled ? forcedValue : false);

  const update = useCallback(() => {
    if (disabled) return;
    setCollapsed(window.scrollY > threshold);
  }, [threshold, disabled]);

  useEffect(() => {
    if (disabled) return;
    window.addEventListener('scroll', update, { passive: true });
    return () => window.removeEventListener('scroll', update);
  }, [update, disabled]);

  return [disabled ? forcedValue : collapsed, update];
}
