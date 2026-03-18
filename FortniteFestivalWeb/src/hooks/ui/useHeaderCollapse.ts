import { useState, useCallback, type RefObject } from 'react';

const DEFAULT_THRESHOLD = 40;

/**
 * Tracks whether a page header should be collapsed based on the scroll
 * position of a container element.
 *
 * @param scrollRef  Ref to the scroll container
 * @param opts.threshold  Pixel distance to trigger collapse (default 40)
 * @param opts.disabled   When true, returns the `forcedValue` without reading scroll
 * @param opts.forcedValue Value to use when disabled (default `false`)
 * @returns `[collapsed, onScroll]` — wire `onScroll` into the container's scroll handler
 */
export function useHeaderCollapse(
  scrollRef: RefObject<HTMLElement | null>,
  opts?: { threshold?: number; disabled?: boolean; forcedValue?: boolean },
): [boolean, () => void] {
  const threshold = opts?.threshold ?? DEFAULT_THRESHOLD;
  const disabled = opts?.disabled ?? false;
  const forcedValue = opts?.forcedValue ?? false;

  const [collapsed, setCollapsed] = useState(disabled ? forcedValue : false);

  const update = useCallback(() => {
    if (disabled) return;
    const el = scrollRef.current;
    if (el) setCollapsed(el.scrollTop > threshold);
  }, [scrollRef, threshold, disabled]);

  return [disabled ? forcedValue : collapsed, update];
}
