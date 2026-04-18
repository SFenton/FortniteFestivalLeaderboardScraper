import { describe, it, expect } from 'vitest';
import {
  resolveCompactRowMode,
  resolveInstrumentChipRows,
  resolvePillFitsTopRow,
  splitInstrumentRows,
  COMPACT_ROW_HYSTERESIS,
  PILL_LAYOUT_HYSTERESIS,
} from '../../../src/pages/songs/layoutMode';

describe('songs layoutMode hysteresis', () => {
  it('holds compact mode until width clears the exit buffer', () => {
    expect(resolveCompactRowMode(700, 720, true)).toBe(true);
    expect(resolveCompactRowMode(720 + COMPACT_ROW_HYSTERESIS - 1, 720, true)).toBe(true);
    expect(resolveCompactRowMode(720 + COMPACT_ROW_HYSTERESIS, 720, true)).toBe(false);
  });

  it('enters compact mode immediately when width drops below the breakpoint', () => {
    expect(resolveCompactRowMode(719, 720, false)).toBe(true);
    expect(resolveCompactRowMode(720, 720, false)).toBe(false);
  });

  it('holds the top-row pill layout until width crosses the lower buffer', () => {
    expect(resolvePillFitsTopRow(310, true)).toBe(true);
    expect(resolvePillFitsTopRow(310 - PILL_LAYOUT_HYSTERESIS + 1, true)).toBe(true);
    expect(resolvePillFitsTopRow(310 - PILL_LAYOUT_HYSTERESIS, true)).toBe(false);
  });

  it('does not restore the top-row pill layout until width clears the upper buffer', () => {
    expect(resolvePillFitsTopRow(310 + PILL_LAYOUT_HYSTERESIS - 1, false)).toBe(false);
    expect(resolvePillFitsTopRow(310 + PILL_LAYOUT_HYSTERESIS, false)).toBe(true);
  });

  it('keeps instrument chips on one row when the available width is sufficient', () => {
    expect(resolveInstrumentChipRows(210, 5)).toBe(1);
    expect(resolveInstrumentChipRows(362, 9)).toBe(1);
  });

  it('splits instrument chips into two rows when the available width is too small', () => {
    expect(resolveInstrumentChipRows(209, 5)).toBe(2);
    expect(resolveInstrumentChipRows(361, 9)).toBe(2);
  });

  it('splits odd counts with the extra chip on the top row', () => {
    const [top, bottom] = splitInstrumentRows([1, 2, 3, 4, 5, 6, 7, 8, 9]);

    expect(top).toEqual([1, 2, 3, 4, 5]);
    expect(bottom).toEqual([6, 7, 8, 9]);
  });

  it('splits even counts evenly across two rows', () => {
    const [top, bottom] = splitInstrumentRows([1, 2, 3, 4, 5, 6, 7, 8]);

    expect(top).toEqual([1, 2, 3, 4]);
    expect(bottom).toEqual([5, 6, 7, 8]);
  });
});
