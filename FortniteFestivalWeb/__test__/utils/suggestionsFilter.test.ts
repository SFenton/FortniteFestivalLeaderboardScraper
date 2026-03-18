import { describe, it, expect, beforeEach } from 'vitest';
import {
  loadSuggestionsFilter,
  saveSuggestionsFilter,
  buildEffectiveInstrumentSettings,
  shouldShowCategoryType,
  filterCategoryForInstrumentTypes,
} from '../../src/utils/suggestionsFilter';
import type { SuggestionsFilterDraft } from '../../src/pages/suggestions/modals/SuggestionsFilterModal';
import type { AppSettings } from '../../src/contexts/SettingsContext';
import type { SuggestionCategory } from '@festival/core/suggestions/types';
import { globalKeyFor } from '@festival/core/suggestions/suggestionFilterConfig';

function mockFilterDraft(overrides: Partial<SuggestionsFilterDraft> = {}): SuggestionsFilterDraft {
  return {
    suggestionsLeadFilter: true,
    suggestionsBassFilter: true,
    suggestionsDrumsFilter: true,
    suggestionsVocalsFilter: true,
    suggestionsProLeadFilter: true,
    suggestionsProBassFilter: true,
    ...overrides,
  } as SuggestionsFilterDraft;
}

function mockAppSettings(overrides: Partial<AppSettings> = {}): AppSettings {
  return {
    showLead: true,
    showBass: true,
    showDrums: true,
    showVocals: true,
    showProLead: true,
    showProBass: true,
    songsHideInstrumentIcons: false,
    songRowVisualOrderEnabled: false,
    songRowVisualOrder: [],
    filterInvalidScores: false,
    filterInvalidScoresLeeway: 0,
    metadataShowScore: true,
    metadataShowPercentage: true,
    metadataShowPercentile: true,
    metadataShowSeasonAchieved: true,
    metadataShowDifficulty: true,
    metadataShowStars: true,
    ...overrides,
  } as AppSettings;
}

describe('loadSuggestionsFilter', () => {
  beforeEach(() => { localStorage.clear(); });

  it('returns defaults when no stored value', () => {
    const defaults = () => mockFilterDraft({ suggestionsLeadFilter: false });
    const result = loadSuggestionsFilter(defaults);
    expect(result.suggestionsLeadFilter).toBe(false);
  });

  it('merges stored value with defaults', () => {
    localStorage.setItem('fst-suggestions-filter', JSON.stringify({ suggestionsLeadFilter: false }));
    const defaults = () => mockFilterDraft({ suggestionsLeadFilter: true });
    const result = loadSuggestionsFilter(defaults);
    expect(result.suggestionsLeadFilter).toBe(false);
  });

  it('handles corrupted JSON gracefully', () => {
    localStorage.setItem('fst-suggestions-filter', 'not-json');
    const defaults = () => mockFilterDraft();
    const result = loadSuggestionsFilter(defaults);
    expect(result.suggestionsLeadFilter).toBe(true);
  });
});

describe('saveSuggestionsFilter', () => {
  beforeEach(() => { localStorage.clear(); });

  it('persists draft to localStorage', () => {
    const draft = mockFilterDraft({ suggestionsLeadFilter: false });
    saveSuggestionsFilter(draft);
    const stored = JSON.parse(localStorage.getItem('fst-suggestions-filter')!);
    expect(stored.suggestionsLeadFilter).toBe(false);
  });
});

describe('buildEffectiveInstrumentSettings', () => {
  it('combines app settings with filter settings', () => {
    const filter = mockFilterDraft({ suggestionsLeadFilter: true, suggestionsBassFilter: false });
    const app = mockAppSettings({ showLead: true, showBass: true });
    const result = buildEffectiveInstrumentSettings(filter, app);
    expect(result.showLead).toBe(true);
    expect(result.showBass).toBe(false);
  });

  it('respects disabled app-level instruments', () => {
    const filter = mockFilterDraft({ suggestionsLeadFilter: true });
    const app = mockAppSettings({ showLead: false });
    const result = buildEffectiveInstrumentSettings(filter, app);
    expect(result.showLead).toBe(false);
  });
});

describe('shouldShowCategoryType', () => {
  it('returns true for unknown category key', () => {
    expect(shouldShowCategoryType('unknown_key', mockFilterDraft())).toBe(true);
  });

  it('returns true when type is enabled in filter', () => {
    const draft = mockFilterDraft();
    (draft as any).suggestionsShowNearFC = true;
    expect(shouldShowCategoryType('near_fc_guitar', draft)).toBe(true);
  });

  it('returns false when type is disabled in filter', () => {
    const draft = mockFilterDraft();
    (draft as any).suggestionsShowNearFC = false;
    expect(shouldShowCategoryType('near_fc_guitar', draft)).toBe(false);
  });
});

describe('filterCategoryForInstrumentTypes', () => {
  const mockCategory = (key: string, songs: { instrumentKey?: string }[] = []): SuggestionCategory => ({
    key,
    title: 'Test',
    description: '',
    songs: songs.map((s, i) => ({
      songId: `song-${i}`,
      title: `Song ${i}`,
      artist: 'Artist',
      instrumentKey: s.instrumentKey,
    })) as any,
  });

  it('returns category unchanged if typeId is unknown', () => {
    const cat = mockCategory('unknown_key', [{ instrumentKey: 'guitar' }]);
    const result = filterCategoryForInstrumentTypes(cat, mockFilterDraft());
    expect(result).toBe(cat);
  });

  it('returns null when category instrument is disabled', () => {
    const cat = mockCategory('near_fc_guitar', [{ instrumentKey: 'guitar' }]);
    const draft = mockFilterDraft();
    (draft as any).suggestionsLeadNearFC = false;
    const result = filterCategoryForInstrumentTypes(cat, draft);
    expect(result).toBeNull();
  });

  it('returns category when category instrument is enabled', () => {
    const cat = mockCategory('near_fc_guitar', [{ instrumentKey: 'guitar' }]);
    const draft = mockFilterDraft();
    (draft as any).suggestionsLeadNearFC = true;
    const result = filterCategoryForInstrumentTypes(cat, draft);
    expect(result).toBe(cat);
  });

  it('filters individual songs by per-instrument toggle when no category instrument', () => {
    // unplayed_ prefix without instrument → songs have instrumentKey, filter per-song
    const cat = mockCategory('unplayed_mixed', [
      { instrumentKey: 'guitar' },
      { instrumentKey: 'bass' },
    ]);
    const draft = mockFilterDraft();
    (draft as any).suggestionsLeadUnplayed = true;
    (draft as any).suggestionsBassUnplayed = false;
    const result = filterCategoryForInstrumentTypes(cat, draft);
    // bass song should be filtered out
    expect(result).not.toBeNull();
    expect(result!.songs).toHaveLength(1);
  });

  it('returns null when all songs are filtered out', () => {
    const cat = mockCategory('unplayed_mixed', [
      { instrumentKey: 'guitar' },
    ]);
    const draft = mockFilterDraft();
    (draft as any).suggestionsLeadUnplayed = false;
    const result = filterCategoryForInstrumentTypes(cat, draft);
    expect(result).toBeNull();
  });

  it('returns original category when no songs are filtered', () => {
    const cat = mockCategory('unplayed_mixed', [
      { instrumentKey: 'guitar' },
    ]);
    const draft = mockFilterDraft();
    (draft as any).suggestionsLeadUnplayed = true;
    const result = filterCategoryForInstrumentTypes(cat, draft);
    expect(result).toBe(cat);
  });

  it('keeps songs without instrumentKey', () => {
    const cat = mockCategory('unplayed_mixed', [
      { instrumentKey: undefined },
      { instrumentKey: 'guitar' },
    ]);
    const draft = mockFilterDraft();
    (draft as any).suggestionsLeadUnplayed = false;
    const result = filterCategoryForInstrumentTypes(cat, draft);
    expect(result).not.toBeNull();
    expect(result!.songs).toHaveLength(1); // only the one without instrumentKey
  });
});

/* Tests extracted from hooks/AllBranches.test.tsx */
describe('suggestionsFilter additional branches', () => {
  it('shouldShowCategoryType: type with global key false → false', () => {
    expect(shouldShowCategoryType('unfc_guitar', { [globalKeyFor('NearFC')]: false } as any)).toBe(false);
  });

  it('shouldShowCategoryType: type with global key true → true', () => {
    expect(shouldShowCategoryType('unfc_guitar', { [globalKeyFor('NearFC')]: true } as any)).toBe(true);
  });

  it('shouldShowCategoryType: type with global key undefined → ?? true', () => {
    expect(shouldShowCategoryType('unfc_guitar', {} as any)).toBe(true);
  });

  it('filterCategory: catInstrument=null + no instrumentKey songs → all pass', () => {
    const cat = { key: 'near_fc_mixed', label: 'near_fc_mixed', songs: [{ songId: 's1', title: 'T', artist: 'A' }, { songId: 's2', title: 'T', artist: 'A' }] } as any;
    const r = filterCategoryForInstrumentTypes(cat, {} as any);
    expect(r).toBe(cat);
  });

  it('filterCategory: per-instrument key undefined → ?? true keeps song', () => {
    const cat = { key: 'unplayed_mixed', label: 'unplayed_mixed', songs: [{ songId: 's1', title: 'T', artist: 'A', instrumentKey: 'guitar' }] } as any;
    const r = filterCategoryForInstrumentTypes(cat, {} as any);
    expect(r).toBe(cat);
  });
});

/* Tests extracted from hooks/CoverageGaps3.test.tsx */
describe('suggestionsFilter — category key variants', () => {
  it('filterCategoryForInstrumentTypes handles per-instrument category (guitar)', () => {
    const cat = { key: 'unfc_guitar', songs: [{ songId: 's1' }] } as any;
    const result = filterCategoryForInstrumentTypes(cat, {} as any);
    expect(result).toBe(cat);
  });

  it('filterCategoryForInstrumentTypes filters out per-instrument when false', () => {
    const cat = { key: 'unfc_guitar', songs: [{ songId: 's1' }] } as any;
    const result = filterCategoryForInstrumentTypes(cat, { suggestionsLeadNearFC: false } as any);
    expect(result).toBeNull();
  });

  it('filterCategoryForInstrumentTypes filters songs by instrumentKey', () => {
    const cat = {
      key: 'near_fc_any',
      songs: [
        { songId: 's1', instrumentKey: 'guitar' },
        { songId: 's2', instrumentKey: 'bass' },
      ],
    } as any;
    const result = filterCategoryForInstrumentTypes(cat, { suggestionsLeadNearFC: false } as any);
    expect(result).not.toBeNull();
    expect(result!.songs).toHaveLength(1);
    expect(result!.songs[0]!.instrumentKey).toBe('bass');
  });

  it('filterCategoryForInstrumentTypes returns null when all songs filtered', () => {
    const cat = {
      key: 'near_fc_any',
      songs: [{ songId: 's1', instrumentKey: 'guitar' }],
    } as any;
    const result = filterCategoryForInstrumentTypes(cat, { suggestionsLeadNearFC: false } as any);
    expect(result).toBeNull();
  });

  it('filterCategoryForInstrumentTypes returns same cat when no songs filtered', () => {
    const cat = {
      key: 'near_fc_any',
      songs: [
        { songId: 's1', instrumentKey: 'guitar' },
        { songId: 's2', instrumentKey: 'bass' },
      ],
    } as any;
    const result = filterCategoryForInstrumentTypes(cat, {} as any);
    expect(result).toBe(cat);
  });

  it('filterCategoryForInstrumentTypes keeps songs without instrumentKey', () => {
    const cat = {
      key: 'near_fc_any',
      songs: [
        { songId: 's1' },
        { songId: 's2', instrumentKey: 'guitar' },
      ],
    } as any;
    const result = filterCategoryForInstrumentTypes(cat, { suggestionsLeadNearFC: false } as any);
    expect(result).not.toBeNull();
    expect(result!.songs).toHaveLength(1);
    expect(result!.songs[0]!.songId).toBe('s1');
  });
});
