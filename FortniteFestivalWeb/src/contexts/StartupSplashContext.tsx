import {
  createContext,
  useCallback,
  useContext,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from 'react';
import StartupSplash from '../components/common/StartupSplash';
import type { InitialAppRevealPhase } from '../hooks/ui/useInitialAppReveal';

type StartupSplashController = {
  holdAnnouncement: () => () => void;
  holdSuppression: () => () => void;
  reportReveal: (phase: InitialAppRevealPhase, onExitComplete: () => void) => void;
  resetReveal: () => void;
};

const StartupSplashContext = createContext<StartupSplashController | null>(null);

/**
 * Owns the one startup splash for the whole bootstrap. Publication,
 * backend-availability, and application-readiness stages drive this single
 * element instead of mounting their own, so its spinner never restarts or
 * moves between stages.
 */
export function StartupSplashProvider({ children }: { children: ReactNode }) {
  const [announcements, setAnnouncements] = useState(0);
  const [suppressions, setSuppressions] = useState(0);
  const [revealPhase, setRevealPhase] = useState<InitialAppRevealPhase>('waiting');
  const exitCompleteRef = useRef<(() => void) | null>(null);

  const controller = useMemo<StartupSplashController>(() => ({
    holdAnnouncement: () => {
      setAnnouncements(count => count + 1);
      return () => setAnnouncements(count => count - 1);
    },
    holdSuppression: () => {
      setSuppressions(count => count + 1);
      return () => setSuppressions(count => count - 1);
    },
    reportReveal: (phase, onExitComplete) => {
      exitCompleteRef.current = onExitComplete;
      setRevealPhase(phase);
    },
    resetReveal: () => {
      exitCompleteRef.current = null;
      setRevealPhase('waiting');
    },
  }), []);

  const handleExitComplete = useCallback(() => {
    exitCompleteRef.current?.();
  }, []);

  return (
    <StartupSplashContext.Provider value={controller}>
      {children}
      {suppressions === 0 && revealPhase !== 'entered' && (
        <StartupSplash
          announce={announcements > 0}
          phase={revealPhase === 'revealing' ? 'revealing' : 'covered'}
          onExitComplete={handleExitComplete}
        />
      )}
    </StartupSplashContext.Provider>
  );
}

/** Make the shared splash the polite loading announcement while `active`. */
export function useStartupSplashAnnouncement(active: boolean): void {
  const controller = useContext(StartupSplashContext);
  useLayoutEffect(() => {
    if (!controller || !active) return;
    return controller.holdAnnouncement();
  }, [active, controller]);
}

/** Remove the shared splash while a bootstrap failure surface is mounted. */
export function useStartupSplashSuppression(): void {
  const controller = useContext(StartupSplashContext);
  useLayoutEffect(() => {
    if (!controller) return;
    return controller.holdSuppression();
  }, [controller]);
}

/**
 * Drive the shared splash from the mounted application's initial reveal.
 * Unmounting the application restores the covered splash for the next mount.
 */
export function useStartupSplashReveal(
  phase: InitialAppRevealPhase,
  onExitComplete: () => void,
): void {
  const controller = useContext(StartupSplashContext);
  useLayoutEffect(() => {
    controller?.reportReveal(phase, onExitComplete);
  }, [controller, onExitComplete, phase]);
  useLayoutEffect(() => {
    if (!controller) return;
    return () => controller.resetReveal();
  }, [controller]);
}
