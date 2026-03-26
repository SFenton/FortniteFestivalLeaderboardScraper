import { FADE_DURATION } from '@festival/theme';
import { useCallback, useEffect, useRef } from 'react';
import { useScrollContainer } from '../../contexts/ScrollContainerContext';

/**
 * On first user-initiated scroll, collapse all pending stagger animation delays
 * to 0ms so remaining items fade in simultaneously.
 *
 * Listens to the app's scroll container (via ScrollContainerContext).
 */
export function useStaggerRush(containerRef: React.RefObject<HTMLElement | null>) {
  const rushedRef = useRef(false);
  const scrollContainerRef = useScrollContainer();

  const rushOnScroll = useCallback(() => {
    if (rushedRef.current) return;
    const container = containerRef.current;
    if (!container) return;
    let rushed = false;
    for (const el of container.querySelectorAll<HTMLElement>('[style*="fadeInUp"]')) {
      if (getComputedStyle(el).opacity !== '0') continue;
      el.style.animation = 'none';
      void el.offsetWidth;
      el.style.animation = `fadeInUp ${FADE_DURATION}ms ease-out forwards`;
      rushed = true;
    }
    if (rushed) rushedRef.current = true;
  }, [containerRef]);

  useEffect(() => {
    const scrollEl = scrollContainerRef.current;
    if (!scrollEl) return;
    scrollEl.addEventListener('scroll', rushOnScroll, { passive: true });
    return () => scrollEl.removeEventListener('scroll', rushOnScroll);
  }, [rushOnScroll, scrollContainerRef]);

  const resetRush = useCallback(() => { rushedRef.current = false; }, []);

  return { rushOnScroll, resetRush };
}
