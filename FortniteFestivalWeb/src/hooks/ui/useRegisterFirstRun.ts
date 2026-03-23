import { useEffect, useRef } from 'react';
import { useFirstRunContext } from '../../contexts/FirstRunContext';
import type { FirstRunSlideDef } from '../../firstRun/types';

/**
 * Register a page's first-run slides with the FirstRunContext.
 * Call once per page component with a stable slides array.
 * Unregisters on unmount so the context stays clean.
 */
export function useRegisterFirstRun(pageKey: string, label: string, slides: FirstRunSlideDef[]) {
  const { register, unregister } = useFirstRunContext();
  const registeredRef = useRef(false);

  useEffect(() => {
    register(pageKey, label, slides);
    registeredRef.current = true;
    return () => {
      unregister(pageKey);
      registeredRef.current = false;
    };
  }, [pageKey, label, slides, register, unregister]);
}
