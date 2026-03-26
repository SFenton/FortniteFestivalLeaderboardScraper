import { useState, useCallback, useEffect } from 'react';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';

const DEFAULT_THRESHOLD = 40;

/**
 * Tracks whether a page header should be collapsed based on the scroll
 * container's scrollTop position.
 */
export function useHeaderCollapse(
  opts?: { threshold?: number; disabled?: boolean; forcedValue?: boolean },
): [boolean, () => void] {
  const threshold = opts?.threshold ?? DEFAULT_THRESHOLD;
  const disabled = opts?.disabled ?? false;
  const forcedValue = opts?.forcedValue ?? false;
  const scrollContainerRef = useScrollContainer();

  const [collapsed, setCollapsed] = useState(disabled ? forcedValue : false);

  const update = useCallback(() => {
    if (disabled) return;
    setCollapsed((scrollContainerRef.current?.scrollTop ?? 0) > threshold);
  }, [threshold, disabled, scrollContainerRef]);

  useEffect(() => {
    if (disabled) return;
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl) return;
    scrollEl.addEventListener('scroll', update, { passive: true });
    return () => scrollEl.removeEventListener('scroll', update);
  }, [update, disabled, scrollContainerRef]);

  return [disabled ? forcedValue : collapsed, update];
}
