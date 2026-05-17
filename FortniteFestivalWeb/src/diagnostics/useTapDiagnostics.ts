import { useEffect, useRef } from 'react';
import { createTapDiagnostics, isTapTelemetryEnabled, TAP_DIAGNOSTICS_SETTINGS_EVENT, TAP_DIAGNOSTICS_STORAGE_KEY, TAP_TELEMETRY_STORAGE_KEY, type TapDiagnosticsState } from './tapDiagnostics';

export function useTapDiagnostics(state: TapDiagnosticsState) {
  const stateRef = useRef(state);
  stateRef.current = state;

  useEffect(() => {
    let diagnostics = createTapDiagnostics(() => stateRef.current, {
      telemetry: {
        enabled: isTapTelemetryEnabled(),
      },
    });

    const reinstallDiagnostics = () => {
      diagnostics?.dispose();
      diagnostics = createTapDiagnostics(() => stateRef.current, {
        telemetry: {
          enabled: isTapTelemetryEnabled(),
        },
      });
    };

    const handleStorage = (event: StorageEvent) => {
      if (event.key && event.key !== TAP_DIAGNOSTICS_STORAGE_KEY && event.key !== TAP_TELEMETRY_STORAGE_KEY) return;
      reinstallDiagnostics();
    };

    window.addEventListener(TAP_DIAGNOSTICS_SETTINGS_EVENT, reinstallDiagnostics);
    window.addEventListener('storage', handleStorage);

    return () => {
      window.removeEventListener(TAP_DIAGNOSTICS_SETTINGS_EVENT, reinstallDiagnostics);
      window.removeEventListener('storage', handleStorage);
      diagnostics?.dispose();
    };
  }, []);
}
