import {
  SUGGESTION_TYPES,
  INSTRUMENT_SETTING_LABELS,
  defaultSuggestionTypeSettings,
  globalKeyFor,
  perInstrumentKeyFor,
  instrumentSettingPrefix,
  getCategoryTypeId,
  getCategoryInstrument,
} from '../suggestionFilterConfig';

describe('suggestionFilterConfig', () => {
  /* ── defaultSuggestionTypeSettings ── */

  test('defaultSuggestionTypeSettings returns all-true settings object', () => {
    const defaults = defaultSuggestionTypeSettings();

    // Every global key should exist and be true
    for (const {id} of SUGGESTION_TYPES) {
      expect(defaults[`suggestionsShow${id}` as keyof typeof defaults]).toBe(true);
    }

    // Every per-instrument key should exist and be true
    const labels = Object.values(INSTRUMENT_SETTING_LABELS);
    for (const {id} of SUGGESTION_TYPES) {
      for (const label of labels) {
        expect(defaults[`suggestions${label}${id}` as keyof typeof defaults]).toBe(true);
      }
    }
  });

  test('defaultSuggestionTypeSettings has correct number of keys', () => {
    const defaults = defaultSuggestionTypeSettings();
    const numTypes = SUGGESTION_TYPES.length;
    const numInstruments = Object.keys(INSTRUMENT_SETTING_LABELS).length;
    // global + per-instrument
    expect(Object.keys(defaults).length).toBe(numTypes * (1 + numInstruments));
  });

  /* ── key helpers ── */

  test('globalKeyFor builds correct key', () => {
    expect(globalKeyFor('NearFC')).toBe('suggestionsShowNearFC');
    expect(globalKeyFor('Unplayed')).toBe('suggestionsShowUnplayed');
  });

  test('perInstrumentKeyFor builds correct key', () => {
    expect(perInstrumentKeyFor('guitar', 'NearFC')).toBe('suggestionsLeadNearFC');
    expect(perInstrumentKeyFor('pro_bass', 'StarProgress')).toBe('suggestionsProBassStarProgress');
    expect(perInstrumentKeyFor('drums', 'Unplayed')).toBe('suggestionsDrumsUnplayed');
  });

  test('instrumentSettingPrefix returns correct prefix', () => {
    expect(instrumentSettingPrefix('guitar')).toBe('suggestionsLead');
    expect(instrumentSettingPrefix('bass')).toBe('suggestionsBass');
    expect(instrumentSettingPrefix('drums')).toBe('suggestionsDrums');
    expect(instrumentSettingPrefix('vocals')).toBe('suggestionsVocals');
    expect(instrumentSettingPrefix('pro_guitar')).toBe('suggestionsProLead');
    expect(instrumentSettingPrefix('pro_bass')).toBe('suggestionsProBass');
  });

  /* ── getCategoryTypeId ── */

  test('getCategoryTypeId maps near_fc variants', () => {
    expect(getCategoryTypeId('near_fc_any')).toBe('NearFC');
    expect(getCategoryTypeId('near_fc_relaxed')).toBe('NearFC');
    expect(getCategoryTypeId('unfc_guitar')).toBe('NearFC');
    expect(getCategoryTypeId('unfc_drums_decade')).toBe('NearFC');
    expect(getCategoryTypeId('samename_nearfc_guitar')).toBe('NearFC');
  });

  test('getCategoryTypeId maps star progress variants', () => {
    expect(getCategoryTypeId('almost_six_star_guitar')).toBe('StarProgress');
    expect(getCategoryTypeId('star_gains_bass')).toBe('StarProgress');
    expect(getCategoryTypeId('more_stars_vocals')).toBe('StarProgress');
  });

  test('getCategoryTypeId maps almost_elite', () => {
    expect(getCategoryTypeId('almost_elite_guitar')).toBe('AlmostElite');
  });

  test('getCategoryTypeId maps pct_push', () => {
    expect(getCategoryTypeId('pct_push_drums')).toBe('PercentilePush');
  });

  test('getCategoryTypeId maps unplayed variants', () => {
    expect(getCategoryTypeId('unplayed_guitar')).toBe('Unplayed');
    expect(getCategoryTypeId('first_plays_mixed')).toBe('Unplayed');
  });

  test('getCategoryTypeId maps variety_pack', () => {
    expect(getCategoryTypeId('variety_pack')).toBe('VarietyPack');
    expect(getCategoryTypeId('variety_pack_guitar')).toBe('VarietyPack');
  });

  test('getCategoryTypeId maps artist_sampler', () => {
    expect(getCategoryTypeId('artist_sampler_beatles')).toBe('ArtistEssentials');
  });

  test('getCategoryTypeId maps artist_unplayed', () => {
    expect(getCategoryTypeId('artist_unplayed_acdc')).toBe('ArtistDiscover');
  });

  test('getCategoryTypeId maps samename (not nearfc)', () => {
    expect(getCategoryTypeId('samename_guitar')).toBe('SameName');
  });

  test('getCategoryTypeId returns null for unknown keys', () => {
    expect(getCategoryTypeId('unknown_category')).toBeNull();
    expect(getCategoryTypeId('')).toBeNull();
  });

  /* ── getCategoryInstrument ── */

  test('getCategoryInstrument extracts guitar', () => {
    expect(getCategoryInstrument('unfc_guitar')).toBe('guitar');
    expect(getCategoryInstrument('unplayed_guitar')).toBe('guitar');
    expect(getCategoryInstrument('almost_elite_guitar')).toBe('guitar');
    expect(getCategoryInstrument('pct_push_guitar')).toBe('guitar');
  });

  test('getCategoryInstrument extracts bass', () => {
    expect(getCategoryInstrument('unfc_bass')).toBe('bass');
  });

  test('getCategoryInstrument extracts drums', () => {
    expect(getCategoryInstrument('unfc_drums')).toBe('drums');
  });

  test('getCategoryInstrument extracts vocals', () => {
    expect(getCategoryInstrument('unfc_vocals')).toBe('vocals');
  });

  test('getCategoryInstrument extracts pro_guitar', () => {
    expect(getCategoryInstrument('unfc_pro_guitar')).toBe('pro_guitar');
  });

  test('getCategoryInstrument extracts pro_bass', () => {
    expect(getCategoryInstrument('unfc_pro_bass')).toBe('pro_bass');
  });

  test('getCategoryInstrument returns null for non-instrument categories', () => {
    expect(getCategoryInstrument('variety_pack')).toBeNull();
    expect(getCategoryInstrument('samename_guitar')).toBeNull();
    expect(getCategoryInstrument('')).toBeNull();
  });

  test('getCategoryInstrument returns null for unknown suffix', () => {
    expect(getCategoryInstrument('unfc_unknown')).toBeNull();
  });

  test('getCategoryTypeId maps near_max variants', () => {
    expect(getCategoryTypeId('near_max_5k')).toBe('NearMaxScore');
    expect(getCategoryTypeId('near_max_10k')).toBe('NearMaxScore');
    expect(getCategoryTypeId('near_max_15k')).toBe('NearMaxScore');
    expect(getCategoryTypeId('near_max_5k_decade_00')).toBe('NearMaxScore');
  });
});
