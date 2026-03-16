import { useState, useEffect, useRef } from 'react';

export type LoadPhase = 'loading' | 'spinnerOut' | 'contentIn';

const SPINNER_FADE_MS = 500;

/**
 * Manages the loading → spinnerOut → contentIn state machine
 * that every page uses for its initial data load transition.
 *
 * @param isReady - true when all async data has loaded
 * @param opts.skipAnimation - skip to contentIn immediately
 * @param opts.spinnerFadeMs - duration of spinnerOut phase (default 500)
 * @returns { phase, shouldStagger } — phase for render gating, shouldStagger for animation decisions
 */
export function useLoadPhase(
  isReady: boolean,
  opts?: { skipAnimation?: boolean; spinnerFadeMs?: number },
): { phase: LoadPhase; shouldStagger: boolean } {
  const skip = opts?.skipAnimation ?? false;
  const fadeMs = opts?.spinnerFadeMs ?? SPINNER_FADE_MS;

  const [phase, setPhase] = useState<LoadPhase>(skip || isReady ? 'contentIn' : 'loading');
  const [shouldStagger, setShouldStagger] = useState(!skip);
  const wasReady = useRef(isReady);

  // Track when data has been ready at least once
  useEffect(() => {
    if (isReady) wasReady.current = true;
  }, [isReady]);

  // Reset to loading when data becomes unready after having been ready (e.g. re-fetch or error)
  useEffect(() => {
    if (!isReady && wasReady.current && phase !== 'loading') {
      wasReady.current = false;
      setPhase('loading');
    }
  }, [isReady, phase]);

  // When data becomes ready, transition loading → spinnerOut
  useEffect(() => {
    if (!isReady || phase !== 'loading') return;
    setShouldStagger(true);
    setPhase('spinnerOut');
  }, [isReady, phase]);

  // When entering spinnerOut, wait for fade then → contentIn
  useEffect(() => {
    if (phase !== 'spinnerOut') return;
    const id = setTimeout(() => setPhase('contentIn'), fadeMs);
    return () => clearTimeout(id);
  }, [phase, fadeMs]);

  return { phase, shouldStagger };
}
