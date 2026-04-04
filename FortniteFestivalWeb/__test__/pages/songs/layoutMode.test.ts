import { describe, it, expect } from 'vitest';
import { resolveCompactRowMode, resolvePillFitsTopRow, COMPACT_ROW_HYSTERESIS, PILL_LAYOUT_HYSTERESIS } from '../../../src/pages/songs/layoutMode';

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
});
