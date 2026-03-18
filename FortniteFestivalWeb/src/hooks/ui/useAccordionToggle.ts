import { useState, useCallback, useRef, useEffect } from 'react';
import { ACCORDION_DELAY_MS } from '@festival/theme';

/**
 * Mutual-exclusion accordion toggle hook.
 * Only one section may be open at a time; closing one delays before opening the other.
 *
 * @param count Number of accordion sections to manage.
 * @param delayMs Delay between close→open transitions (default: ACCORDION_DELAY_MS).
 * @returns [openStates, toggleFn, resetFn]
 */
export function useAccordionToggle(
  count: number,
  delayMs: number = ACCORDION_DELAY_MS,
): [boolean[], (index: number) => void, () => void] {
  const [openStates, setOpenStates] = useState<boolean[]>(() => Array(count).fill(false));
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);

  useEffect(() => () => clearTimeout(timer.current), []);

  const toggle = useCallback((index: number) => {
    clearTimeout(timer.current);
    setOpenStates(prev => {
      const current = prev[index];
      if (current) {
        // Close the active accordion
        const next = [...prev];
        next[index] = false;
        return next;
      }
      // Another accordion is open — close it first, then open after delay
      const anyOpen = prev.some(Boolean);
      if (anyOpen) {
        const closed = prev.map(() => false);
        timer.current = setTimeout(() => {
          setOpenStates(p => {
            const n = [...p];
            n[index] = true;
            return n;
          });
        }, delayMs);
        return closed;
      }
      // Nothing open — open immediately
      const next = [...prev];
      next[index] = true;
      return next;
    });
  }, [delayMs]);

  const reset = useCallback(() => {
    clearTimeout(timer.current);
    setOpenStates(Array(count).fill(false));
  }, [count]);

  return [openStates, toggle, reset];
}
