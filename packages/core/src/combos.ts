import type { ServerInstrumentKey } from './api/serverTypes';
import { SERVER_INSTRUMENT_KEYS } from './api/serverTypes';

/**
 * Combo ID system — deterministic bitmask-based identifiers for instrument combinations.
 *
 * Each of the 6 instruments occupies a fixed bit position (0–5) in the canonical order
 * defined by SERVER_INSTRUMENT_KEYS. A combo ID is the zero-padded 2-digit lowercase
 * hex representation of the bitmask. For example:
 *   - Lead + Bass         → bits 0+1 → 0x03 → "03"
 *   - All Pad (G+B+D+V)  → bits 0-3 → 0x0f → "0f"
 *   - All 6               → all bits → 0x3f → "3f"
 */

/**
 * Canonical instrument order — the index of each key is its bit position.
 * This MUST match SERVER_INSTRUMENT_KEYS order and the C# ComboIds class.
 */
export const COMBO_INSTRUMENTS: readonly ServerInstrumentKey[] = SERVER_INSTRUMENT_KEYS;

/** Compute the combo ID (2-digit hex) for a set of instruments. */
export function comboIdFromInstruments(instruments: readonly ServerInstrumentKey[]): string {
  let mask = 0;
  for (const key of instruments) {
    const bit = COMBO_INSTRUMENTS.indexOf(key);
    if (bit < 0) throw new Error(`Unknown instrument: ${key}`);
    mask |= 1 << bit;
  }
  return mask.toString(16).padStart(2, '0');
}

/** Recover the instrument list from a combo ID. Returns instruments in canonical order. */
export function instrumentsFromComboId(comboId: string): ServerInstrumentKey[] {
  const mask = parseInt(comboId, 16);
  if (Number.isNaN(mask) || mask < 0 || mask > 0x3f)
    throw new Error(`Invalid combo ID: ${comboId}`);
  const result: ServerInstrumentKey[] = [];
  for (let bit = 0; bit < COMBO_INSTRUMENTS.length; bit++) {
    if (mask & (1 << bit)) result.push(COMBO_INSTRUMENTS[bit]);
  }
  return result;
}

/** True when the combo ID represents 2 or more instruments. */
export function isMultiInstrumentCombo(comboId: string): boolean {
  const mask = parseInt(comboId, 16);
  return !Number.isNaN(mask) && bitCount(mask) >= 2;
}

/** Number of set bits. */
function bitCount(n: number): number {
  let count = 0;
  let v = n;
  while (v) { count += v & 1; v >>>= 1; }
  return count;
}

/**
 * All valid multi-instrument combo IDs (bitmask ≥ 2 set bits).
 * Map from combo ID → sorted array of instruments.
 */
export const ALL_COMBO_IDS: ReadonlyMap<string, readonly ServerInstrumentKey[]> = (() => {
  const map = new Map<string, ServerInstrumentKey[]>();
  const n = COMBO_INSTRUMENTS.length;
  for (let mask = 3; mask < (1 << n); mask++) {
    if (bitCount(mask) < 2) continue;
    const instruments: ServerInstrumentKey[] = [];
    for (let bit = 0; bit < n; bit++) {
      if (mask & (1 << bit)) instruments.push(COMBO_INSTRUMENTS[bit]);
    }
    map.set(mask.toString(16).padStart(2, '0'), instruments);
  }
  return map;
})();
