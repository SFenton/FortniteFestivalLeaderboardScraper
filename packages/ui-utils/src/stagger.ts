/**
 * Returns a stagger delay (ms) for item at `index`, or `undefined` if the item
 * is beyond the initial viewport and should appear instantly.
 *
 * @param index       Zero-based item index
 * @param interval    Milliseconds between consecutive items (e.g. 125)
 * @param maxItems    Max items to animate (items beyond this appear instantly).
 *                    Estimate based on viewport height / item height.
 */
export function staggerDelay(
  index: number,
  interval: number,
  maxItems: number,
): number | undefined {
  return index < maxItems ? (index + 1) * interval : undefined;
}

/**
 * Estimate how many items of a given height fit in the current viewport.
 * Adds +1 as a buffer for partially visible items.
 */
export function estimateVisibleCount(itemHeight: number): number {
  const vh = typeof window !== 'undefined' ? window.innerHeight : 900;
  return Math.ceil(vh / itemHeight) + 1;
}
