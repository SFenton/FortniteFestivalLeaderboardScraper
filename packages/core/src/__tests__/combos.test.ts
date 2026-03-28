import {
  comboIdFromInstruments,
  instrumentsFromComboId,
  isMultiInstrumentCombo,
  ALL_COMBO_IDS,
  COMBO_INSTRUMENTS,
} from '../combos';
import type { ServerInstrumentKey } from '../api/serverTypes';

describe('combos', () => {
  describe('comboIdFromInstruments', () => {
    test('Lead + Bass = "03"', () => {
      expect(comboIdFromInstruments(['Solo_Guitar', 'Solo_Bass'])).toBe('03');
    });

    test('Lead + Pro Lead = "11"', () => {
      expect(comboIdFromInstruments(['Solo_Guitar', 'Solo_PeripheralGuitar'])).toBe('11');
    });

    test('All pad (G+B+D+V) = "0f"', () => {
      expect(comboIdFromInstruments(['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals'])).toBe('0f');
    });

    test('All pro = "30"', () => {
      expect(comboIdFromInstruments(['Solo_PeripheralGuitar', 'Solo_PeripheralBass'])).toBe('30');
    });

    test('All 6 = "3f"', () => {
      expect(comboIdFromInstruments([
        'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
        'Solo_PeripheralGuitar', 'Solo_PeripheralBass',
      ])).toBe('3f');
    });

    test('order-independent', () => {
      expect(comboIdFromInstruments(['Solo_Bass', 'Solo_Guitar'])).toBe('03');
    });

    test('single instrument', () => {
      expect(comboIdFromInstruments(['Solo_Guitar'])).toBe('01');
    });

    test('throws for unknown instrument', () => {
      expect(() => comboIdFromInstruments(['NotReal' as ServerInstrumentKey])).toThrow('Unknown instrument');
    });
  });

  describe('instrumentsFromComboId', () => {
    test('reverses "03" to Lead + Bass', () => {
      expect(instrumentsFromComboId('03')).toEqual(['Solo_Guitar', 'Solo_Bass']);
    });

    test('reverses "0f" to 4 pad instruments', () => {
      expect(instrumentsFromComboId('0f')).toEqual([
        'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
      ]);
    });

    test('reverses "3f" to all 6', () => {
      expect(instrumentsFromComboId('3f')).toEqual([
        'Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals',
        'Solo_PeripheralGuitar', 'Solo_PeripheralBass',
      ]);
    });

    test('throws for invalid hex', () => {
      expect(() => instrumentsFromComboId('zz')).toThrow('Invalid combo ID');
    });

    test('throws for out-of-range mask', () => {
      expect(() => instrumentsFromComboId('ff')).toThrow('Invalid combo ID');
    });
  });

  describe('round-trip', () => {
    test('all COMBO_INSTRUMENTS round-trips', () => {
      const id = comboIdFromInstruments([...COMBO_INSTRUMENTS]);
      expect(instrumentsFromComboId(id)).toEqual([...COMBO_INSTRUMENTS]);
    });

    test.each([
      ['03'], ['07'], ['0f'], ['11'], ['30'], ['3f'],
    ])('round-trip for combo ID %s', (id) => {
      const instruments = instrumentsFromComboId(id);
      expect(comboIdFromInstruments(instruments)).toBe(id);
    });
  });

  describe('isMultiInstrumentCombo', () => {
    test('single instrument = false', () => {
      expect(isMultiInstrumentCombo('01')).toBe(false);
    });

    test('two instruments = true', () => {
      expect(isMultiInstrumentCombo('03')).toBe(true);
    });

    test('invalid = false', () => {
      expect(isMultiInstrumentCombo('zz')).toBe(false);
    });
  });

  describe('ALL_COMBO_IDS', () => {
    test('contains 57 entries (all multi-instrument combos from 6 instruments)', () => {
      expect(ALL_COMBO_IDS.size).toBe(57);
    });

    test('every entry has 2+ instruments', () => {
      for (const [, instruments] of ALL_COMBO_IDS) {
        expect(instruments.length).toBeGreaterThanOrEqual(2);
      }
    });

    test('no single-instrument entries', () => {
      // Single instrument masks: 01, 02, 04, 08, 10, 20
      expect(ALL_COMBO_IDS.has('01')).toBe(false);
      expect(ALL_COMBO_IDS.has('02')).toBe(false);
      expect(ALL_COMBO_IDS.has('04')).toBe(false);
    });

    test('includes known combos', () => {
      expect(ALL_COMBO_IDS.has('03')).toBe(true); // Lead + Bass
      expect(ALL_COMBO_IDS.has('3f')).toBe(true); // All 6
      expect(ALL_COMBO_IDS.has('30')).toBe(true); // Both pros
    });
  });
});
