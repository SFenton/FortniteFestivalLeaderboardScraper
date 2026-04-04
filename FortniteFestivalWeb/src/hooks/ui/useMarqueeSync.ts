import { useCallback, useRef, useState } from 'react';

const DEFAULT_GAP = 28;

/**
 * Coordinates multiple MarqueeText instances so they share the same translate
 * distance, producing pixel-perfect lockstep scrolling.
 *
 * Each instance reports its measured text width via `reporters[i]`.
 * When 2+ instances overflow, `syncDistance` is set to `max(widths) + gap`
 * so all instances translate the same pixel distance.
 * When fewer than 2 overflow, `syncDistance` is `undefined` (no sync).
 */
export function useMarqueeSync(count: number, gap = DEFAULT_GAP) {
  const widths = useRef<number[]>(new Array(count).fill(0));
  const [syncDistance, setSyncDistance] = useState<number | undefined>(undefined);

  const makeReporter = useCallback(
    (index: number) => (width: number) => {
      widths.current[index] = width;
      const nonZero = widths.current.filter(w => w > 0);
      if (nonZero.length >= 2) {
        setSyncDistance(Math.max(...nonZero) + gap);
      } else {
        setSyncDistance(undefined);
      }
    },
    [gap],
  );

  // Build stable reporter array — one per slot.
  const reporters = useRef<((w: number) => void)[]>([]);
  if (reporters.current.length !== count) {
    reporters.current = Array.from({ length: count }, (_, i) => makeReporter(i));
  }

  return { reporters: reporters.current, syncDistance };
}
