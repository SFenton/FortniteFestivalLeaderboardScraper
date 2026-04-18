/**
 * Shared hysteresis helpers for Songs page responsive layout decisions.
 * These keep the UI from bouncing between adjacent layouts when the width
 * hovers near a threshold during ResizeObserver updates.
 */

import { Gap, InstrumentSize } from '@festival/theme';

export const COMPACT_ROW_HYSTERESIS = 32;
export const PILL_LAYOUT_HYSTERESIS = 12;
export const SONG_ROW_HORIZONTAL_PADDING = Gap.xl * 2;

export function getInstrumentRowWidth(
  chipCount: number,
  chipSize = InstrumentSize.chip,
  gap = Gap.sm,
): number {
  if (chipCount <= 0) return 0;
  return chipCount * chipSize + Math.max(chipCount - 1, 0) * gap;
}

export function resolveInstrumentChipRows(
  width: number | undefined,
  chipCount: number,
  horizontalPadding = SONG_ROW_HORIZONTAL_PADDING,
  chipSize = InstrumentSize.chip,
  gap = Gap.sm,
): 1 | 2 {
  if (chipCount <= 1) return 1;
  if (!width || width <= 0) return 1;

  const availableWidth = width - horizontalPadding;
  if (availableWidth <= 0) return 2;

  return getInstrumentRowWidth(chipCount, chipSize, gap) <= availableWidth ? 1 : 2;
}

export function splitInstrumentRows<T>(items: readonly T[]): readonly [T[], T[]] {
  if (items.length <= 1) return [items.slice(), []];

  const midpoint = Math.ceil(items.length / 2);
  return [items.slice(0, midpoint), items.slice(midpoint)];
}

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
