const THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;

/** Clamp a raw percentile (0–100) to the nearest filter bucket and return e.g. "Top 5%". */
export function formatPercentile(pct: number): string {
  const clamped = Math.max(1, Math.min(100, pct));
  const bucket = THRESHOLDS.find(t => clamped <= t) ?? 100;
  return `Top ${bucket}%`;
}
