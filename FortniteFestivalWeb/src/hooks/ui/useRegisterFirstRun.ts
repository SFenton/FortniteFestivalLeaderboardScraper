import { useLayoutEffect, useRef } from 'react';
import { useFirstRunContext } from '../../contexts/FirstRunContext';
import type { FirstRunSlideDef } from '../../firstRun/types';

/**
 * Register a page's first-run slides with the FirstRunContext.
 * Call once per page component with a stable slides array.
 * Unregisters on unmount so the context stays clean.
 *
 * Uses useLayoutEffect so registration completes before paint,
 * allowing useFirstRun to evaluate slides before the first frame.
 */
export function useRegisterFirstRun(pageKey: string, label: string, slides: FirstRunSlideDef[]) {
  const { register, unregister } = useFirstRunContext();
  const registeredRef = useRef(false);

  useLayoutEffect(() => {
    register(pageKey, label, slides);
    registeredRef.current = true;
    return () => {
      unregister(pageKey);
      registeredRef.current = false;
    };
  }, [pageKey, label, slides, register, unregister]);
}
