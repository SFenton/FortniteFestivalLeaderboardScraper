import { useState, useCallback, useEffect, useMemo, useRef } from 'react';
import { LoadPhase } from '@festival/core';
import { SPINNER_FADE_MS } from '@festival/theme';

export interface ViewTransitionOptions {
  /** Duration of the SpinnerOut phase in ms. Default: SPINNER_FADE_MS (500). */
  fadeMs?: number;
}

export interface ViewTransitionResult {
  /** Current load phase — pass to `<Page loadPhase={phase}>` and use for content gating. */
  phase: LoadPhase;
  /** True after `trigger()` until the stagger animation window has passed. */
  shouldStagger: boolean;
  /** Kick off a SpinnerOut → ContentIn transition. */
  trigger: () => void;
  /** Convenience: `phase !== LoadPhase.ContentIn`. */
  isTransitioning: boolean;
}

/**
 * Manages imperative view transitions (grid/list toggle, sort/filter changes)
 * where data is already loaded but the view is being rebuilt.
 *
 * Complements `useLoadPhase` which handles declarative data-load transitions.
 *
 * @example
 * const { phase, shouldStagger, trigger } = useViewTransition();
 * // on toggle click:
 * trigger();
 * // in JSX:
 * <Page loadPhase={phase}>
 *   {phase === LoadPhase.ContentIn && <Content />}
 * </Page>
 */
export function useViewTransition(opts?: ViewTransitionOptions): ViewTransitionResult {
  const fadeMs = opts?.fadeMs ?? SPINNER_FADE_MS;

  const [phase, setPhase] = useState<LoadPhase>(LoadPhase.ContentIn);
  const [shouldStagger, setShouldStagger] = useState(false);

  // Track whether a transition is in flight to avoid stacking timers
  const transitionRef = useRef(false);

  const trigger = useCallback(() => {
    transitionRef.current = true;
    setShouldStagger(true);
    setPhase(LoadPhase.SpinnerOut);
  }, []);

  // SpinnerOut → ContentIn timer
  useEffect(() => {
    if (phase !== LoadPhase.SpinnerOut) return;
    const id = setTimeout(() => {
      transitionRef.current = false;
      setPhase(LoadPhase.ContentIn);
    }, fadeMs);
    return () => clearTimeout(id);
  }, [phase, fadeMs]);

  const isTransitioning = phase !== LoadPhase.ContentIn;

  return useMemo(
    () => ({ phase, shouldStagger, trigger, isTransitioning }),
    [phase, shouldStagger, trigger, isTransitioning],
  );
}
