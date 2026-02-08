import {defaultAdvancedMissingFilters, defaultPrimaryInstrumentOrder, normalizeInstrumentOrder} from '../songListConfig';

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
});
