/**
 * Shared hysteresis helpers for Songs page responsive layout decisions.
 * These keep the UI from bouncing between adjacent layouts when the width
 * hovers near a threshold during ResizeObserver updates.
 */

import { Gap, InstrumentSize } from '@festival/theme';

export const COMPACT_ROW_HYSTERESIS = 32;
export const PILL_LAYOUT_HYSTERESIS = 12;
export const SONG_ROW_HORIZONTAL_PADDING = Gap.xl * 2;
export const INSTRUMENT_ROW_HYSTERESIS = 16;

export type InstrumentChipRowCount = 1 | 2 | 3;

export function getInstrumentRowWidth(
  chipCount: number,
  chipSize: number = InstrumentSize.chip,
  gap: number = Gap.sm,
): number {
  if (chipCount <= 0) return 0;
  return chipCount * chipSize + Math.max(chipCount - 1, 0) * gap;
}

export function resolveInstrumentChipRows(
  width: number | undefined,
  chipCount: number,
  horizontalPadding = SONG_ROW_HORIZONTAL_PADDING,
  chipSize: number = InstrumentSize.chip,
  gap: number = Gap.sm,
  previousRowCount: InstrumentChipRowCount = 1,
  hysteresis = INSTRUMENT_ROW_HYSTERESIS,
): InstrumentChipRowCount {
  if (chipCount <= 1) return 1;
  if (chipCount <= 4) return 1;
  if (!width || width <= 0) return previousRowCount;

  const availableWidth = width - horizontalPadding;
  if (availableWidth <= 0) return previousRowCount > 1 ? previousRowCount : 2;

  const widths: Record<InstrumentChipRowCount, number> = {
    1: getInstrumentRowWidth(chipCount, chipSize, gap),
    2: getInstrumentRowWidth(Math.ceil(chipCount / 2), chipSize, gap),
    3: getInstrumentRowWidth(Math.ceil(chipCount / 3), chipSize, gap),
  };

  if (previousRowCount === 1) {
    if (widths[1] <= availableWidth) return 1;
    if (widths[2] <= availableWidth) return 2;
    return 3;
  }

  if (previousRowCount === 2) {
    if (widths[1] <= availableWidth - hysteresis) return 1;
    if (widths[2] <= availableWidth) return 2;
    return 3;
  }

  if (widths[2] <= availableWidth - hysteresis) return 2;
  return 3;
}

export function splitInstrumentRows<T>(items: readonly T[], rowCount = 2): T[][] {
  if (rowCount <= 1 || items.length <= 1) return [items.slice()];

  const safeRowCount = Math.max(1, Math.min(rowCount, items.length));
  const baseSize = Math.floor(items.length / safeRowCount);
  const remainder = items.length % safeRowCount;
  const rows: T[][] = [];
  let start = 0;

  for (let rowIndex = 0; rowIndex < safeRowCount; rowIndex++) {
    const size = baseSize + (rowIndex < remainder ? 1 : 0);
    rows.push(items.slice(start, start + size));
    start += size;
  }

  return rows;
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
