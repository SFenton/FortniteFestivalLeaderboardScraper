import { useCallback, useEffect, useRef } from 'react';

const VIEWPORT_SETTLE_DELAYS_MS = [80, 180, 320] as const;

export function useScrollUpdateScheduler(
  update: () => void,
  disabled = false,
): {
  scheduleUpdate: () => void;
  updateNow: () => void;
} {
  const frameRef = useRef(0);
  const viewportTimeoutsRef = useRef<number[]>([]);

  const clearScheduled = useCallback(() => {
    cancelAnimationFrame(frameRef.current);
    frameRef.current = 0;
    for (const timeout of viewportTimeoutsRef.current) {
      window.clearTimeout(timeout);
    }
    viewportTimeoutsRef.current = [];
  }, []);

  const scheduleUpdate = useCallback(() => {
    if (disabled || frameRef.current) return;
    frameRef.current = requestAnimationFrame(() => {
      frameRef.current = 0;
      update();
    });
  }, [disabled, update]);

  const updateNow = useCallback(() => {
    clearScheduled();
    if (!disabled) update();
  }, [clearScheduled, disabled, update]);

  const scheduleViewportUpdate = useCallback(() => {
    clearScheduled();
    if (disabled) return;
    scheduleUpdate();
    viewportTimeoutsRef.current = VIEWPORT_SETTLE_DELAYS_MS.map(
      delay => window.setTimeout(scheduleUpdate, delay),
    );
  }, [clearScheduled, disabled, scheduleUpdate]);

  useEffect(() => {
    if (disabled) {
      clearScheduled();
      return;
    }
    const visualViewport = window.visualViewport;
    visualViewport?.addEventListener('resize', scheduleViewportUpdate);
    visualViewport?.addEventListener('scroll', scheduleViewportUpdate);
    window.addEventListener('resize', scheduleViewportUpdate);
    return () => {
      visualViewport?.removeEventListener('resize', scheduleViewportUpdate);
      visualViewport?.removeEventListener('scroll', scheduleViewportUpdate);
      window.removeEventListener('resize', scheduleViewportUpdate);
      clearScheduled();
    };
  }, [clearScheduled, disabled, scheduleViewportUpdate]);

  useEffect(() => () => {
    clearScheduled();
  }, [clearScheduled]);

  return { scheduleUpdate, updateNow };
}
