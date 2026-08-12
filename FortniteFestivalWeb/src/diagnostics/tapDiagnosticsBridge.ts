export const TAP_DIAGNOSTICS_SETTINGS_EVENT = 'fst:tap-diagnostics-settings-changed';
export const TAP_DIAGNOSTICS_STORAGE_KEY = 'fst.tapDiagnostics';
export const TAP_TELEMETRY_STORAGE_KEY = 'fst.tapTelemetry';

export function markTapDiagnosticsAction(
  label: string,
  phase: 'start' | 'success' | 'failure' | 'note',
  details?: Record<string, unknown>,
): void {
  const diagnostics = (
    window as Window & {
      __fstTapDiagnostics?: {
        markAction: (
          actionLabel: string,
          actionPhase: 'start' | 'success' | 'failure' | 'note',
          actionDetails?: Record<string, unknown>,
        ) => void;
      };
    }
  ).__fstTapDiagnostics;
  diagnostics?.markAction(label, phase, details);
}
