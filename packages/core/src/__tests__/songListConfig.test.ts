import {defaultAdvancedMissingFilters, defaultPrimaryInstrumentOrder, isInstrumentSortMode, isInstrumentVisible, normalizeInstrumentOrder, normalizeMetadataSortPriority, normalizeSongRowVisualOrder, percentileBucket, reorderPIOForVisibilityChange, showSettingKeyForInstrument} from '../songListConfig';
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
      seasonFilter: {},
      percentileFilter: {},
      starsFilter: {},
      difficultyFilter: {},
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

  /* ── percentileBucket ── */

  test('percentileBucket returns 0 for non-positive input', () => {
    expect(percentileBucket(0)).toBe(0);
    expect(percentileBucket(-0.5)).toBe(0);
  });

  test('percentileBucket maps small fractions to correct bucket', () => {
    expect(percentileBucket(0.005)).toBe(1);   // 0.5% → Top 1
    expect(percentileBucket(0.015)).toBe(2);   // 1.5% → Top 2
    expect(percentileBucket(0.05)).toBe(5);    // 5% → Top 5
    expect(percentileBucket(0.12)).toBe(15);   // 12% → Top 15
    expect(percentileBucket(0.45)).toBe(50);   // 45% → Top 50
    expect(percentileBucket(1.0)).toBe(100);   // 100% → Top 100
  });

  test('percentileBucket clamps values above 100%', () => {
    expect(percentileBucket(1.5)).toBe(100);
  });

  test('percentileBucket returns 1 for very small positive fractions', () => {
    expect(percentileBucket(0.001)).toBe(1);
  });

  /* ── isInstrumentSortMode ── */

  test('isInstrumentSortMode returns true for instrument-specific modes', () => {
    expect(isInstrumentSortMode('score')).toBe(true);
    expect(isInstrumentSortMode('percentage')).toBe(true);
    expect(isInstrumentSortMode('percentile')).toBe(true);
    expect(isInstrumentSortMode('isfc')).toBe(true);
    expect(isInstrumentSortMode('stars')).toBe(true);
    expect(isInstrumentSortMode('seasonachieved')).toBe(true);
    expect(isInstrumentSortMode('intensity')).toBe(true);
  });

  test('isInstrumentSortMode returns false for non-instrument modes', () => {
    expect(isInstrumentSortMode('title')).toBe(false);
    expect(isInstrumentSortMode('artist')).toBe(false);
    expect(isInstrumentSortMode('year')).toBe(false);
    expect(isInstrumentSortMode('hasfc')).toBe(false);
  });

  /* ── normalizeMetadataSortPriority ── */

  test('normalizeMetadataSortPriority returns defaults for empty/undefined', () => {
    const base = normalizeMetadataSortPriority(undefined);
    expect(base.map(i => i.key)).toEqual(['title', 'artist', 'year', 'score', 'percentage', 'percentile', 'isfc', 'stars', 'seasonachieved', 'intensity']);
    expect(normalizeMetadataSortPriority([])).toEqual(base);
  });

  test('normalizeMetadataSortPriority reorders and appends missing', () => {
    const out = normalizeMetadataSortPriority(['stars', 'score']);
    expect(out.map(i => i.key)).toEqual(['stars', 'score', 'title', 'artist', 'year', 'percentage', 'percentile', 'isfc', 'seasonachieved', 'intensity']);
  });

  /* ── normalizeSongRowVisualOrder ── */

  test('normalizeSongRowVisualOrder returns defaults for empty/undefined', () => {
    const base = normalizeSongRowVisualOrder(undefined);
    expect(base.map(i => i.key)).toEqual(['score', 'percentage', 'percentile', 'stars', 'seasonachieved', 'intensity']);
    expect(normalizeSongRowVisualOrder([])).toEqual(base);
  });

  test('normalizeSongRowVisualOrder reorders and appends missing', () => {
    const out = normalizeSongRowVisualOrder(['stars', 'score']);
    expect(out.map(i => i.key)).toEqual(['stars', 'score', 'percentage', 'percentile', 'seasonachieved', 'intensity']);
  });

  test('normalizeMetadataSortPriority ignores unknown keys', () => {
    const out = normalizeMetadataSortPriority(['stars', 'unknown_key' as any, 'score']);
    // unknown_key should be silently ignored
    expect(out[0].key).toBe('stars');
    expect(out[1].key).toBe('score');
    expect(out.length).toBe(10); // all known keys still present
  });

  test('normalizeSongRowVisualOrder ignores unknown keys', () => {
    const out = normalizeSongRowVisualOrder(['stars', 'bogus' as any, 'score']);
    expect(out[0].key).toBe('stars');
    expect(out[1].key).toBe('score');
    expect(out.length).toBe(6);
  });

  test('percentileBucket clamps very small positive values to bucket 1', () => {
    // rawPercentile = 0.001 → topPct = 0.1 → clamped to topPct = 1 → bucket 1
    expect(percentileBucket(0.001)).toBe(1);
    // rawPercentile = 0.0001 → topPct = 0.01 → clamped to topPct = 1 → bucket 1
    expect(percentileBucket(0.0001)).toBe(1);
  });

  test('reorderPIOForVisibilityChange when visible predecessor is not in currentOrder', () => {
    // Default order: guitar, drums, vocals, bass, pro_guitar, pro_bass
    // currentOrder is MISSING drums (even though it's visible) — simulates a corrupted/partial list
    const partialOrder = ['guitar', 'vocals', 'bass', 'pro_guitar', 'pro_bass'] as const;
    const settings = {...allVisible}; // drums is visible
    // Re-enable vocals: without = ['guitar', 'bass', 'pro_guitar', 'pro_bass']
    // Vocals default predecessors: guitar (idx 0 → found), drums (visible but not in without → idx=-1 → skip)
    // Actually vocals's default index is 2, so predecessors are drums (idx 1) and guitar (idx 0)
    // We're re-enabling a key that's already in the list, so let's use a different one.
    // Actually let's hide bass and re-enable it:
    // without = [guitar, vocals, pro_guitar, pro_bass] (bass removed since it's changedKey)
    // Bass default index = 3 → predecessors: vocals (2), drums (1), guitar (0)
    // drums: visible (showDrums=true), but drums IS in the list... hmm
    
    // Better test: use partialOrder missing 'drums', re-enable pro_guitar
    // pro_guitar default index = 4, predecessors: bass(3), vocals(2), drums(1), guitar(0)
    // without = ['guitar', 'vocals', 'bass', 'pro_bass'] (pro_guitar removed)
    // drums: visible, but NOT in without (not in partialOrder at all) → idx=-1 → continue
    // next: vocals: visible, in without at idx 1 → insertAfter=1 → insert pro_guitar at idx 2
    const result = reorderPIOForVisibilityChange([...partialOrder], 'pro_guitar', true, settings);
    // drums not in list, but is a predecessor → skipped → falls to vocals
    expect(result).toContain('pro_guitar');
    expect(result.indexOf('pro_guitar')).toBeGreaterThan(result.indexOf('vocals'));
  });
});
