import { useEffect, useRef } from 'react';
import { createTapDiagnostics, type TapDiagnosticsState } from './tapDiagnostics';

export function useTapDiagnostics(state: TapDiagnosticsState) {
  const stateRef = useRef(state);
  stateRef.current = state;

  useEffect(() => {
    const diagnostics = createTapDiagnostics(() => stateRef.current);
    return () => diagnostics?.dispose();
  }, []);
}
