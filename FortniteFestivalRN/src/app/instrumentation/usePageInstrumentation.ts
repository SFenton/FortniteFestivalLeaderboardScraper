import {useEffect, useRef} from 'react';

import {useFestival} from '../festival/FestivalContext';

export function usePageInstrumentation(pageName: string): void {
  const {
    actions: {logUi},
  } = useFestival();
  const startedAtMsRef = useRef<number>(Date.now());

  useEffect(() => {
    startedAtMsRef.current = Date.now();
    logUi(`[PAGE] enter ${pageName}`);

    return () => {
      const elapsedMs = Math.max(0, Date.now() - startedAtMsRef.current);
      logUi(`[PAGE] exit ${pageName} (${elapsedMs}ms)`);
    };
  }, [logUi, pageName]);
}
