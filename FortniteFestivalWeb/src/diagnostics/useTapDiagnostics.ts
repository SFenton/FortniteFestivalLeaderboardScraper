import { useEffect, useRef } from 'react';
import type { TapDiagnosticsState } from './tapDiagnostics';
import {
  TAP_DIAGNOSTICS_SETTINGS_EVENT,
  TAP_DIAGNOSTICS_STORAGE_KEY,
  TAP_TELEMETRY_STORAGE_KEY,
} from './tapDiagnosticsBridge';

export function useTapDiagnostics(state: TapDiagnosticsState) {
  const stateRef = useRef(state);
  stateRef.current = state;

  useEffect(() => {
    let disposed = false;
    let installRevision = 0;
    let diagnostics: { dispose: () => void } | null = null;

    const reinstallDiagnostics = async () => {
      const revision = ++installRevision;
      diagnostics?.dispose();
      diagnostics = null;
      if (!shouldLoadTapDiagnostics()) return;

      const module = await import('./tapDiagnostics');
      if (disposed || revision !== installRevision) return;
      diagnostics = module.createTapDiagnostics(() => stateRef.current, {
        telemetry: {
          enabled: module.isTapTelemetryEnabled(),
        },
      });
    };

    const handleStorage = (event: StorageEvent) => {
      if (event.key && event.key !== TAP_DIAGNOSTICS_STORAGE_KEY && event.key !== TAP_TELEMETRY_STORAGE_KEY) return;
      void reinstallDiagnostics();
    };

    void reinstallDiagnostics();
    const handleSettings = () => { void reinstallDiagnostics(); };
    window.addEventListener(TAP_DIAGNOSTICS_SETTINGS_EVENT, handleSettings);
    window.addEventListener('storage', handleStorage);

    return () => {
      disposed = true;
      installRevision += 1;
      window.removeEventListener(TAP_DIAGNOSTICS_SETTINGS_EVENT, handleSettings);
      window.removeEventListener('storage', handleStorage);
      diagnostics?.dispose();
    };
  }, []);
}

function shouldLoadTapDiagnostics(): boolean {
  const params = new URLSearchParams(window.location.search);
  const validation = params.get('validation') ?? '';
  if (params.get('tapDiagnostics') === '1' || params.get('tapTelemetry') === '1') return true;
  if (validation.split(/[,:;]/).some(value => value === 'tap-diagnostics' || value === 'tap-telemetry')) return true;

  try {
    return isEnabledValue(localStorage.getItem(TAP_DIAGNOSTICS_STORAGE_KEY))
      || isEnabledValue(localStorage.getItem(TAP_TELEMETRY_STORAGE_KEY));
  } catch {
    return false;
  }
}

function isEnabledValue(value: string | null): boolean {
  return value != null && ['1', 'true', 'yes', 'on'].includes(value.trim().toLowerCase());
}
