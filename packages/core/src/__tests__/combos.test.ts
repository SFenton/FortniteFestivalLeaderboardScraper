import {
  comboIdFromInstruments,
  instrumentsFromComboId,
  isMultiInstrumentCombo,
  isWithinGroupCombo,
  isWithinGroupComboId,
  INSTRUMENT_GROUPS,
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
    test('contains 12 entries (within-group combos only)', () => {
      expect(ALL_COMBO_IDS.size).toBe(12);
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

    test('includes known within-group combos', () => {
      expect(ALL_COMBO_IDS.has('03')).toBe(true); // Lead + Bass
      expect(ALL_COMBO_IDS.has('0f')).toBe(true); // All OG Band
      expect(ALL_COMBO_IDS.has('30')).toBe(true); // Both pros
    });

    test('excludes cross-group combos', () => {
      expect(ALL_COMBO_IDS.has('3f')).toBe(false); // All 6 (cross-group)
      expect(ALL_COMBO_IDS.has('11')).toBe(false); // Lead + Pro Lead (cross-group)
      expect(ALL_COMBO_IDS.has('21')).toBe(false); // Lead + Pro Bass (cross-group)
    });

    test('every entry is within-group', () => {
      for (const [id] of ALL_COMBO_IDS) {
        expect(isWithinGroupComboId(id)).toBe(true);
      }
    });
  });

  describe('isWithinGroupCombo', () => {
    test('OG Band pairs are within-group', () => {
      expect(isWithinGroupCombo(0x03)).toBe(true); // Lead + Bass
      expect(isWithinGroupCombo(0x05)).toBe(true); // Lead + Drums
      expect(isWithinGroupCombo(0x0f)).toBe(true); // All 4 OG
    });

    test('Pro Strings pair is within-group', () => {
      expect(isWithinGroupCombo(0x30)).toBe(true); // Pro Lead + Pro Bass
    });

    test('cross-group combos are rejected', () => {
      expect(isWithinGroupCombo(0x11)).toBe(false); // Lead + Pro Lead
      expect(isWithinGroupCombo(0x3f)).toBe(false); // All 6
      expect(isWithinGroupCombo(0x31)).toBe(false); // Lead + Pro Strings
    });

    test('peripheral multi-instrument combos are rejected', () => {
      expect(isWithinGroupCombo(0xc0)).toBe(false);
      expect(isWithinGroupCombo(0x1c0)).toBe(false);
    });

    test('single instrument is not a combo', () => {
      expect(isWithinGroupCombo(0x01)).toBe(false);
      expect(isWithinGroupCombo(0x10)).toBe(false);
    });
  });

  describe('isWithinGroupComboId', () => {
    test('delegates to mask version', () => {
      expect(isWithinGroupComboId('03')).toBe(true);
      expect(isWithinGroupComboId('11')).toBe(false);
      expect(isWithinGroupComboId('30')).toBe(true);
    });
  });

  describe('INSTRUMENT_GROUPS', () => {
    test('has 2 groups', () => {
      expect(INSTRUMENT_GROUPS).toHaveLength(2);
    });

    test('OG Band group covers bits 0-3', () => {
      expect(INSTRUMENT_GROUPS[0]).toBe(0x0f);
    });

    test('Pro Strings group covers bits 4-5', () => {
      expect(INSTRUMENT_GROUPS[1]).toBe(0x30);
    });
  });
});
