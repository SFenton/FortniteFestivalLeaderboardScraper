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
