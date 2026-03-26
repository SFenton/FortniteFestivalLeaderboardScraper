import { FADE_DURATION } from '@festival/theme';
import { useCallback, useEffect, useRef } from 'react';

/**
 * On first user-initiated scroll, collapse all pending stagger animation delays
 * to 0ms so remaining items fade in simultaneously.
 *
 * Listens to window scroll events (browser-native scroll model).
 * Elements already animating or finished are unaffected.
 *
 * @param containerRef  Ref to the container whose children have stagger animations.
 *                      Used to scope the querySelectorAll for pending animations.
 */
export function useStaggerRush(containerRef: React.RefObject<HTMLElement | null>) {
  const rushedRef = useRef(false);

  const rushOnScroll = useCallback(() => {
    if (rushedRef.current) return;
    const container = containerRef.current;
    if (!container) return;
    let rushed = false;
    for (const el of container.querySelectorAll<HTMLElement>('[style*="fadeInUp"]')) {
      // Only rush elements still waiting (opacity 0 = animation hasn't visually started)
      if (getComputedStyle(el).opacity !== '0') continue;
      // Restart animation with zero delay so the fade still plays
      el.style.animation = 'none';
      void el.offsetWidth; // force reflow
      el.style.animation = `fadeInUp ${FADE_DURATION}ms ease-out forwards`;
      rushed = true;
    }
    // Only mark as rushed if we actually found and rushed elements
    if (rushed) rushedRef.current = true;
  }, [containerRef]);

  // Automatically listen to window scroll
  useEffect(() => {
    window.addEventListener('scroll', rushOnScroll, { passive: true });
    return () => window.removeEventListener('scroll', rushOnScroll);
  }, [rushOnScroll]);

  const resetRush = useCallback(() => { rushedRef.current = false; }, []);

  return { rushOnScroll, resetRush };
}
