import { useCallback, useEffect, useRef, useState } from 'react';
import { TRANSITION_MS } from '@festival/theme';
import { useMediaQuery } from './useMediaQuery';

export type InitialAppRevealPhase = 'waiting' | 'revealing' | 'entered';

const TRANSITION_FALLBACK_BUFFER_MS = 100;

export function useInitialAppReveal(ready: boolean) {
  const reducedMotion = useMediaQuery('(prefers-reduced-motion: reduce)');
  const [phase, setPhase] = useState<InitialAppRevealPhase>('waiting');
  const frameRef = useRef<number | null>(null);

  const complete = useCallback(() => {
    setPhase(current => current === 'revealing' ? 'entered' : current);
  }, []);

  useEffect(() => {
    if (!ready || phase !== 'waiting') return;
    if (reducedMotion) {
      setPhase('entered');
      return;
    }

    frameRef.current = window.requestAnimationFrame(() => {
      frameRef.current = null;
      setPhase(current => current === 'waiting' ? 'revealing' : current);
    });

    return () => {
      if (frameRef.current !== null) {
        window.cancelAnimationFrame(frameRef.current);
        frameRef.current = null;
      }
    };
  }, [phase, ready, reducedMotion]);

  useEffect(() => {
    if (phase !== 'revealing') return;
    const timeout = window.setTimeout(
      complete,
      TRANSITION_MS + TRANSITION_FALLBACK_BUFFER_MS,
    );
    return () => window.clearTimeout(timeout);
  }, [complete, phase]);

  return {
    complete,
    entered: phase === 'entered',
    phase,
  };
}
