import { useState, useCallback, useEffect } from 'react';

/**
 * Manages a spinner that fades in when active and fades out when inactive.
 * Matches the pattern from MobilePlayerSearchModal — the spinner stays mounted
 * during the fade-out transition and unmounts after it completes.
 *
 * @param active Whether the spinner should be visible (e.g. `loading || debouncing`).
 * @returns `visible` (mount flag), `opacity` (0 or 1), and `onTransitionEnd` handler.
 */
export function useFadeSpinner(active: boolean) {
  const [visible, setVisible] = useState(false);
  const [opacity, setOpacity] = useState(0);

  useEffect(() => {
    if (active) {
      setVisible(true);
      // Double rAF ensures the element is mounted with opacity 0 before transitioning to 1
      requestAnimationFrame(() => requestAnimationFrame(() => setOpacity(1)));
    } else if (visible) {
      setOpacity(0);
    }
  }, [active]); // eslint-disable-line react-hooks/exhaustive-deps

  const onTransitionEnd = useCallback(() => {
    if (opacity === 0 && !active) setVisible(false);
  }, [opacity, active]);

  const reset = useCallback(() => {
    setVisible(false);
    setOpacity(0);
  }, []);

  return { visible, opacity, onTransitionEnd, reset };
}
