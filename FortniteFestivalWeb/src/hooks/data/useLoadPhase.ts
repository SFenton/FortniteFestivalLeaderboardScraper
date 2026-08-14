import { useState, useEffect, useMemo, useCallback } from 'react';
import { LoadPhase } from '@festival/core/runtime';
import { SPINNER_FADE_MS, CONTENT_OUT_MS } from '@festival/theme';

export type { LoadPhase };

type LoadPhaseState = {
  resetKey: unknown;
  phase: LoadPhase;
  shouldStagger: boolean;
  wasReady: boolean;
};

function createLoadPhaseState(
  resetKey: unknown,
  isReady: boolean,
  skipAnimation: boolean,
): LoadPhaseState {
  return {
    resetKey,
    phase: skipAnimation || isReady ? LoadPhase.ContentIn : LoadPhase.Loading,
    shouldStagger: !skipAnimation,
    wasReady: isReady,
  };
}

/**
 * Manages the loading → spinnerOut → contentIn state machine
 * that every page uses for its initial data load transition.
 *
 * @param isReady - true when all async data has loaded
 * @param opts.skipAnimation - skip to contentIn immediately
 * @param opts.spinnerFadeMs - duration of spinnerOut phase (default 500)
 * @param opts.contentOutMs - duration of contentOut phase (default 300)
 * @param opts.resetKey - synchronously reset transition state when page identity changes
 * @returns { phase, shouldStagger, triggerContentOut } — phase for render gating, shouldStagger for animation decisions, triggerContentOut to start a fade-out/re-stagger cycle
 */
export function useLoadPhase(
  isReady: boolean,
  opts?: {
    skipAnimation?: boolean;
    spinnerFadeMs?: number;
    contentOutMs?: number;
    resetKey?: unknown;
  },
): { phase: LoadPhase; shouldStagger: boolean; triggerContentOut: () => void } {
  const skip = opts?.skipAnimation ?? false;
  const fadeMs = opts?.spinnerFadeMs ?? SPINNER_FADE_MS;
  const outMs = opts?.contentOutMs ?? CONTENT_OUT_MS;
  const resetKey = opts?.resetKey;

  const [storedState, setStoredState] = useState<LoadPhaseState>(
    () => createLoadPhaseState(resetKey, isReady, skip),
  );
  const state = Object.is(storedState.resetKey, resetKey)
    ? storedState
    : createLoadPhaseState(resetKey, isReady, skip);
  if (state !== storedState) {
    setStoredState(state);
  }
  const { phase, shouldStagger, wasReady } = state;

  // Track when data has been ready at least once
  useEffect(() => {
    if (!isReady || wasReady) return;
    setStoredState(current => (
      Object.is(current.resetKey, resetKey)
        ? { ...current, wasReady: true }
        : current
    ));
  }, [isReady, resetKey, wasReady]);

  // Reset to loading when data becomes unready after having been ready (e.g. re-fetch or error)
  useEffect(() => {
    if (isReady || !wasReady || phase === LoadPhase.Loading || phase === LoadPhase.ContentOut) return;
    setStoredState(current => (
      Object.is(current.resetKey, resetKey)
        ? { ...current, phase: LoadPhase.Loading, wasReady: false }
        : current
    ));
  }, [isReady, phase, resetKey, wasReady]);

  // When data becomes ready, transition loading → spinnerOut
  useEffect(() => {
    if (!isReady || phase !== LoadPhase.Loading) return;
    setStoredState(current => (
      Object.is(current.resetKey, resetKey)
        ? {
            ...current,
            phase: LoadPhase.SpinnerOut,
            shouldStagger: true,
            wasReady: true,
          }
        : current
    ));
  }, [isReady, phase, resetKey]);

  // When entering spinnerOut, wait for fade then → contentIn
  useEffect(() => {
    if (phase !== LoadPhase.SpinnerOut) return;
    const id = setTimeout(() => {
      setStoredState(current => (
        Object.is(current.resetKey, resetKey)
          ? { ...current, phase: LoadPhase.ContentIn }
          : current
      ));
    }, fadeMs);
    return () => clearTimeout(id);
  }, [phase, fadeMs, resetKey]);

  // When entering contentOut, wait for fade then → loading (data will be refetched)
  useEffect(() => {
    if (phase !== LoadPhase.ContentOut) return;
    const id = setTimeout(() => {
      setStoredState(current => (
        Object.is(current.resetKey, resetKey)
          ? {
              ...current,
              phase: LoadPhase.Loading,
              wasReady: false,
            }
          : current
      ));
    }, outMs);
    return () => clearTimeout(id);
  }, [phase, outMs, resetKey]);

  // Allow callers to trigger a content-out → re-stagger cycle
  const triggerContentOut = useCallback(() => {
    setStoredState(current => (
      Object.is(current.resetKey, resetKey) && current.phase === LoadPhase.ContentIn
        ? {
            ...current,
            phase: LoadPhase.ContentOut,
            shouldStagger: true,
          }
        : current
    ));
  }, [resetKey]);

  return useMemo(() => ({ phase, shouldStagger, triggerContentOut }), [phase, shouldStagger, triggerContentOut]);
}
