/**
 * Shared formatting utilities.
 */

/**
 * Format accuracy for display.
 * Accepts a value already divided by 10,000 (a real percentage 0–100).
 */
export function formatAccuracyText(pct: number): string {
  const r1 = pct.toFixed(1);
  return r1.endsWith('.0') ? `${Math.round(pct)}%` : `${r1}%`;
}

/**
 * Format a duration in seconds as "m:ss" (or "h:mm:ss" for 1h+).
 * Returns empty string for missing, zero, or negative values.
 */
export function formatDuration(seconds?: number | null): string {
  if (seconds == null || seconds <= 0) return '';
  const total = Math.round(seconds);
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  const ss = s.toString().padStart(2, '0');
  if (h > 0) return `${h}:${m.toString().padStart(2, '0')}:${ss}`;
  return `${m}:${ss}`;
}
