/**
 * Shared hysteresis helpers for Songs page responsive layout decisions.
 * These keep the UI from bouncing between adjacent layouts when the width
 * hovers near a threshold during ResizeObserver updates.
 */

export const COMPACT_ROW_HYSTERESIS = 32;
export const PILL_LAYOUT_HYSTERESIS = 12;

export function resolveCompactRowMode(
  width: number,
  minDesktopWidth: number,
  wasCompact: boolean,
  hysteresis = COMPACT_ROW_HYSTERESIS,
): boolean {
  if (width <= 0) return wasCompact;
  if (wasCompact) return width < minDesktopWidth + hysteresis;
  return width < minDesktopWidth;
}

export function resolvePillFitsTopRow(
  width: number | undefined,
  wasTopRow: boolean,
  threshold = 310,
  hysteresis = PILL_LAYOUT_HYSTERESIS,
): boolean {
  if (!width || width <= 0) return wasTopRow;
  if (wasTopRow) return width > threshold - hysteresis;
  return width >= threshold + hysteresis;
}
