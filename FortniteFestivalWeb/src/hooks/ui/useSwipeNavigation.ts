import { useCallback, useRef, type TouchEvent } from 'react';
import { SWIPE_THRESHOLD } from '@festival/theme';

export function useSwipeNavigation({ onBack, onForward }: { onBack: () => void; onForward: () => void }) {
  const touchStartRef = useRef<number | null>(null);

  const handleTouchStart = useCallback((event: TouchEvent) => {
    touchStartRef.current = event.touches[0]?.clientX ?? null;
  }, []);

  const handleTouchEnd = useCallback((event: TouchEvent) => {
    if (touchStartRef.current === null) return;
    const endX = event.changedTouches[0]?.clientX;
    if (endX === undefined) return;
    const delta = endX - touchStartRef.current;
    touchStartRef.current = null;
    if (Math.abs(delta) < SWIPE_THRESHOLD) return;
    if (delta < 0) onForward();
    else onBack();
  }, [onBack, onForward]);

  return { handleTouchStart, handleTouchEnd };
}