import {defaultAdvancedMissingFilters, defaultPrimaryInstrumentOrder, isInstrumentVisible, normalizeInstrumentOrder, reorderPIOForVisibilityChange, showSettingKeyForInstrument} from '../songListConfig';
import type {InstrumentShowSettings} from '../songListConfig';

const allVisible: InstrumentShowSettings = {
  showLead: true, showBass: true, showDrums: true, showVocals: true, showProLead: true, showProBass: true,
};

describe('songListConfig', () => {
  test('defaultAdvancedMissingFilters enables all instruments and no missing filters', () => {
    const d = defaultAdvancedMissingFilters();
    expect(d).toEqual({
      missingPadFCs: false,
      missingProFCs: false,
      missingPadScores: false,
      missingProScores: false,
      includeLead: true,
      includeBass: true,
      includeDrums: true,
      includeVocals: true,
      includeProGuitar: true,
      includeProBass: true,
    });
  });

  test('normalizeInstrumentOrder returns defaults for empty/undefined input', () => {
    const base = defaultPrimaryInstrumentOrder();
    expect(normalizeInstrumentOrder(undefined)).toEqual(base);
    expect(normalizeInstrumentOrder([])).toEqual(base);
  });

  test('normalizeInstrumentOrder reorders known keys and appends missing ones', () => {
    const out = normalizeInstrumentOrder(['drums', 'guitar']);
    expect(out.map(i => i.key)).toEqual([
      'drums',
      'guitar',
      'vocals',
      'bass',
      'pro_guitar',
      'pro_bass',
    ]);
    expect(out.map(i => i.displayName)).toEqual([
      'Drums',
      'Lead',
      'Vocals',
      'Bass',
      'Pro Lead',
      'Pro Bass',
    ]);
  });

  test('normalizeInstrumentOrder ignores duplicate keys', () => {
    const out = normalizeInstrumentOrder(['drums', 'drums', 'guitar']);
    expect(out.map(i => i.key)).toEqual([
      'drums',
      'guitar',
      'vocals',
      'bass',
      'pro_guitar',
      'pro_bass',
    ]);
  });

  /* ── showSettingKeyForInstrument ── */

  test('showSettingKeyForInstrument maps every InstrumentKey to its show setting', () => {
    expect(showSettingKeyForInstrument('guitar')).toBe('showLead');
    expect(showSettingKeyForInstrument('bass')).toBe('showBass');
    expect(showSettingKeyForInstrument('drums')).toBe('showDrums');
    expect(showSettingKeyForInstrument('vocals')).toBe('showVocals');
    expect(showSettingKeyForInstrument('pro_guitar')).toBe('showProLead');
    expect(showSettingKeyForInstrument('pro_bass')).toBe('showProBass');
  });

  /* ── isInstrumentVisible ── */

  test('isInstrumentVisible returns true when all instruments are visible', () => {
    expect(isInstrumentVisible('guitar', allVisible)).toBe(true);
    expect(isInstrumentVisible('drums', allVisible)).toBe(true);
    expect(isInstrumentVisible('pro_bass', allVisible)).toBe(true);
  });

  test('isInstrumentVisible returns false when an instrument is hidden', () => {
    expect(isInstrumentVisible('drums', {...allVisible, showDrums: false})).toBe(false);
    expect(isInstrumentVisible('pro_guitar', {...allVisible, showProLead: false})).toBe(false);
  });

  /* ── reorderPIOForVisibilityChange ── */

  test('hiding an instrument moves it to the end of PIO', () => {
    const defaultOrder = defaultPrimaryInstrumentOrder().map(i => i.key);
    const result = reorderPIOForVisibilityChange(defaultOrder, 'drums', false, allVisible);
    expect(result).toEqual(['guitar', 'vocals', 'bass', 'pro_guitar', 'pro_bass', 'drums']);
  });

  test('hiding an instrument from a custom order moves it to the end', () => {
    const custom = ['bass', 'guitar', 'vocals', 'drums', 'pro_guitar', 'pro_bass'] as const;
    const result = reorderPIOForVisibilityChange([...custom], 'guitar', false, allVisible);
    expect(result).toEqual(['bass', 'vocals', 'drums', 'pro_guitar', 'pro_bass', 'guitar']);
  });

  test('re-enabling an instrument restores it to its default-relative position', () => {
    // drums hidden at end, re-enable it → should go back to after guitar (default pos 1)
    const order = ['guitar', 'vocals', 'bass', 'pro_guitar', 'pro_bass', 'drums'] as const;
    const result = reorderPIOForVisibilityChange([...order], 'drums', true, {...allVisible, showDrums: false});
    expect(result).toEqual(['guitar', 'drums', 'vocals', 'bass', 'pro_guitar', 'pro_bass']);
  });

  test('re-enabling the first default instrument inserts at the beginning', () => {
    // guitar hidden at end, re-enable it → should go to position 0
    const order = ['drums', 'vocals', 'bass', 'pro_guitar', 'pro_bass', 'guitar'] as const;
    const result = reorderPIOForVisibilityChange([...order], 'guitar', true, {...allVisible, showLead: false});
    expect(result).toEqual(['guitar', 'drums', 'vocals', 'bass', 'pro_guitar', 'pro_bass']);
  });

  test('re-enabling the last default instrument inserts at the end of visible portion', () => {
    // pro_bass hidden at end alongside drums (drums also hidden)
    const order = ['guitar', 'vocals', 'bass', 'pro_guitar', 'drums', 'pro_bass'] as const;
    const settings = {...allVisible, showDrums: false, showProBass: false};
    const result = reorderPIOForVisibilityChange([...order], 'pro_bass', true, settings);
    // pro_bass default pos is after pro_guitar; pro_guitar is visible at index 3
    expect(result).toEqual(['guitar', 'vocals', 'bass', 'pro_guitar', 'pro_bass', 'drums']);
  });

  test('re-enabling when some predecessors are also hidden inserts correctly', () => {
    // Both drums and vocals hidden; re-enable vocals
    // Default order: guitar, drums, vocals, bass, pro_guitar, pro_bass
    // Vocals default predecessors: guitar, drums. drums is hidden → skipped → finds guitar.
    const order = ['guitar', 'bass', 'pro_guitar', 'pro_bass', 'drums', 'vocals'] as const;
    const settings = {...allVisible, showDrums: false, showVocals: false};
    const result = reorderPIOForVisibilityChange([...order], 'vocals', true, settings);
    // Should insert after guitar (the only visible predecessor in default order)
    expect(result).toEqual(['guitar', 'vocals', 'bass', 'pro_guitar', 'pro_bass', 'drums']);
  });
});
