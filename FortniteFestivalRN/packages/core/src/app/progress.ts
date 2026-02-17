export type ProgressState = {
  progressPct: number;
  progressLabel: string;
  shouldLog: boolean;
  nextLogCounter: number;
};

const clamp = (v: number, min: number, max: number): number => Math.max(min, Math.min(max, v));

const formatOneDecimal = (v: number): string => {
  if (!Number.isFinite(v)) return '0.0';

  const rounded = Math.round(v * 10) / 10;
  // Ensure exactly one decimal (C# {pct:0.0})
  return rounded.toFixed(1);
};

/**
 * Pure port of ProcessViewModel.OnSongProgress:
 * - label: `${current}/${total} (${pct:0.0}%)`
 * - progressPct numeric
 * - logs every 25 items (and first/last), only when `started === false`
 * - resets counter to 0 when `current === total`
 */
export const computeProgressState = (params: {
  current: number;
  total: number;
  started: boolean;
  logCounter: number;
}): ProgressState => {
  const total = params.total > 0 ? params.total : 0;
  const current = params.current;

  const pct = total > 0 ? (current / total) * 100 : 0;
  const progressPct = clamp(pct, 0, 100);
  const progressLabel = total > 0 ? `${current}/${total} (${formatOneDecimal(pct)}%)` : '0%';

  let nextLogCounter = params.logCounter;
  let shouldLog = false;

  if (!params.started) {
    nextLogCounter = nextLogCounter + 1;
    if (nextLogCounter === 1 || current === total || nextLogCounter % 25 === 0) shouldLog = true;
  }

  if (total > 0 && current === total) nextLogCounter = 0;

  return {progressPct, progressLabel, shouldLog, nextLogCounter};
};
