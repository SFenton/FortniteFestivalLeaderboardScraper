import { useState, useEffect, useRef, useMemo, useCallback } from 'react';
import { LoadPhase } from '@festival/core';
import { SPINNER_FADE_MS, CONTENT_OUT_MS } from '@festival/theme';

export type { LoadPhase };

/**
 * Manages the loading → spinnerOut → contentIn state machine
 * that every page uses for its initial data load transition.
 *
 * @param isReady - true when all async data has loaded
 * @param opts.skipAnimation - skip to contentIn immediately
 * @param opts.spinnerFadeMs - duration of spinnerOut phase (default 500)
 * @param opts.contentOutMs - duration of contentOut phase (default 300)
 * @returns { phase, shouldStagger, triggerContentOut } — phase for render gating, shouldStagger for animation decisions, triggerContentOut to start a fade-out/re-stagger cycle
 */
export function useLoadPhase(
  isReady: boolean,
  opts?: { skipAnimation?: boolean; spinnerFadeMs?: number; contentOutMs?: number },
): { phase: LoadPhase; shouldStagger: boolean; triggerContentOut: () => void } {
  const skip = opts?.skipAnimation ?? false;
  const fadeMs = opts?.spinnerFadeMs ?? SPINNER_FADE_MS;
  const outMs = opts?.contentOutMs ?? CONTENT_OUT_MS;

  const [phase, setPhase] = useState<LoadPhase>(skip || isReady ? LoadPhase.ContentIn : LoadPhase.Loading);
  const [shouldStagger, setShouldStagger] = useState(!skip);
  const wasReady = useRef(isReady);

  // Track when data has been ready at least once
  useEffect(() => {
    if (isReady) wasReady.current = true;
  }, [isReady]);

  // Reset to loading when data becomes unready after having been ready (e.g. re-fetch or error)
  useEffect(() => {
    if (!isReady && wasReady.current && phase !== LoadPhase.Loading && phase !== LoadPhase.ContentOut) {
      wasReady.current = false;
      setPhase(LoadPhase.Loading);
    }
  }, [isReady, phase]);

  // When data becomes ready, transition loading → spinnerOut
  useEffect(() => {
    if (!isReady || phase !== LoadPhase.Loading) return;
    setShouldStagger(true);
    setPhase(LoadPhase.SpinnerOut);
  }, [isReady, phase]);

  // When entering spinnerOut, wait for fade then → contentIn
  useEffect(() => {
    if (phase !== LoadPhase.SpinnerOut) return;
    const id = setTimeout(() => setPhase(LoadPhase.ContentIn), fadeMs);
    return () => clearTimeout(id);
  }, [phase, fadeMs]);

  // When entering contentOut, wait for fade then → loading (data will be refetched)
  useEffect(() => {
    if (phase !== LoadPhase.ContentOut) return;
    const id = setTimeout(() => {
      wasReady.current = false;
      setPhase(LoadPhase.Loading);
    }, outMs);
    return () => clearTimeout(id);
  }, [phase, outMs]);

  // Allow callers to trigger a content-out → re-stagger cycle
  const triggerContentOut = useCallback(() => {
    if (phase === LoadPhase.ContentIn) {
      setShouldStagger(true);
      setPhase(LoadPhase.ContentOut);
    }
  }, [phase]);

  return useMemo(() => ({ phase, shouldStagger, triggerContentOut }), [phase, shouldStagger, triggerContentOut]);
}
