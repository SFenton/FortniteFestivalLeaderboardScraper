/**
 * Unit tests for pure helper functions extracted from SuggestionsPage.
 */
import { describe, it, expect, beforeEach, vi } from 'vitest';
import {
  loadSuggestionsFilter,
  saveSuggestionsFilter,
  buildEffectiveInstrumentSettings,
  shouldShowCategoryType,
  filterCategoryForInstrumentTypes,
  computeEffectiveSeason,
  getCardDelay,
  buildAlbumArtMap,
  FILTER_STORAGE_KEY,
} from '../../../src/pages/suggestions/suggestionsHelpers';
import { defaultSuggestionsFilterDraft } from '../../../src/pages/suggestions/modals/SuggestionsFilterModal';
import type { SuggestionCategory } from '@festival/core/suggestions/types';

beforeEach(() => { localStorage.clear(); });

// ── loadSuggestionsFilter ──

describe('loadSuggestionsFilter', () => {
  it('returns defaults when localStorage is empty', () => {
    const result = loadSuggestionsFilter();
    expect(result.suggestionsLeadFilter).toBe(true);
    expect(result.suggestionsBassFilter).toBe(true);
  });

  it('merges saved values with defaults', () => {
    localStorage.setItem(FILTER_STORAGE_KEY, JSON.stringify({ suggestionsLeadFilter: false }));
    const result = loadSuggestionsFilter();
    expect(result.suggestionsLeadFilter).toBe(false);
    expect(result.suggestionsBassFilter).toBe(true);
  });

  it('returns defaults when localStorage has invalid JSON', () => {
    localStorage.setItem(FILTER_STORAGE_KEY, 'not-json{{{');
    const result = loadSuggestionsFilter();
    expect(result.suggestionsLeadFilter).toBe(true);
  });

  it('returns defaults when localStorage has null-ish value', () => {
    localStorage.setItem(FILTER_STORAGE_KEY, 'null');
    const result = loadSuggestionsFilter();
    expect(result.suggestionsLeadFilter).toBe(true);
  });
});

// ── saveSuggestionsFilter ──

describe('saveSuggestionsFilter', () => {
  it('persists filter draft to localStorage', () => {
    const draft = { ...defaultSuggestionsFilterDraft(), suggestionsLeadFilter: false };
    saveSuggestionsFilter(draft);
    const raw = localStorage.getItem(FILTER_STORAGE_KEY);
    expect(raw).toBeTruthy();
    expect(JSON.parse(raw!).suggestionsLeadFilter).toBe(false);
  });
});

// ── buildEffectiveInstrumentSettings ──

describe('buildEffectiveInstrumentSettings', () => {
  const allOn = {
    showLead: true, showBass: true, showDrums: true,
    showVocals: true, showProLead: true, showProBass: true,
  } as any;

  it('returns all true when both app settings and filter are true', () => {
    const filter = defaultSuggestionsFilterDraft();
    const result = buildEffectiveInstrumentSettings(filter, allOn);
    expect(result.showLead).toBe(true);
    expect(result.showBass).toBe(true);
    expect(result.showDrums).toBe(true);
    expect(result.showVocals).toBe(true);
    expect(result.showProLead).toBe(true);
    expect(result.showProBass).toBe(true);
  });

  it('returns false when app setting is false', () => {
    const filter = defaultSuggestionsFilterDraft();
    const settings = { ...allOn, showLead: false };
    const result = buildEffectiveInstrumentSettings(filter, settings);
    expect(result.showLead).toBe(false);
    expect(result.showBass).toBe(true);
  });

  it('returns false when filter is false', () => {
    const filter = { ...defaultSuggestionsFilterDraft(), suggestionsLeadFilter: false };
    const result = buildEffectiveInstrumentSettings(filter, allOn);
    expect(result.showLead).toBe(false);
  });

  it('returns false when both are false', () => {
    const filter = { ...defaultSuggestionsFilterDraft(), suggestionsLeadFilter: false };
    const settings = { ...allOn, showLead: false };
    const result = buildEffectiveInstrumentSettings(filter, settings);
    expect(result.showLead).toBe(false);
  });
});

// ── shouldShowCategoryType ──

describe('shouldShowCategoryType', () => {
  it('returns true for unknown category keys (no typeId)', () => {
    const filter = defaultSuggestionsFilterDraft();
    expect(shouldShowCategoryType('random_key', filter)).toBe(true);
  });

  it('returns true for a known type when filter is on', () => {
    const filter = defaultSuggestionsFilterDraft();
    expect(shouldShowCategoryType('near_fc_any', filter)).toBe(true);
  });

  it('returns false for a known type when filter is off', () => {
    const filter = { ...defaultSuggestionsFilterDraft(), suggestionsShowNearFC: false };
    expect(shouldShowCategoryType('near_fc_any', filter)).toBe(false);
  });

  it('recognizes various category type prefixes', () => {
    const filter = defaultSuggestionsFilterDraft();
    expect(shouldShowCategoryType('almost_six_star_guitar', filter)).toBe(true);
    expect(shouldShowCategoryType('pct_push_drums', filter)).toBe(true);
    expect(shouldShowCategoryType('unplayed_bass', filter)).toBe(true);
    expect(shouldShowCategoryType('stale_vocals', filter)).toBe(true);
  });

  it('defaults to true when filter key is missing (null-coalescing)', () => {
    // Provide a filter object that is missing the globalKey entry entirely
    const filter = { suggestionsLeadFilter: true } as any;
    expect(shouldShowCategoryType('near_fc_any', filter)).toBe(true);
  });
});

// ── filterCategoryForInstrumentTypes ──

describe('filterCategoryForInstrumentTypes', () => {
  const makeCat = (key: string, songs: any[] = []): SuggestionCategory => ({
    key, title: 'Test', description: 'Desc', songs,
  });

  it('returns the category unchanged for unknown type keys', () => {
    const cat = makeCat('random_key', [{ songId: 's1', title: 'A', artist: 'A' }]);
    const filter = defaultSuggestionsFilterDraft();
    expect(filterCategoryForInstrumentTypes(cat, filter)).toBe(cat);
  });

  it('returns category for instrument-specific key when filter is on', () => {
    const cat = makeCat('unfc_guitar', [{ songId: 's1', title: 'A', artist: 'A' }]);
    const filter = defaultSuggestionsFilterDraft();
    expect(filterCategoryForInstrumentTypes(cat, filter)).toBe(cat);
  });

  it('returns null for instrument-specific key when filter is off', () => {
    const cat = makeCat('unfc_guitar', [{ songId: 's1', title: 'A', artist: 'A' }]);
    const filter = { ...defaultSuggestionsFilterDraft(), suggestionsLeadNearFC: false };
    expect(filterCategoryForInstrumentTypes(cat, filter)).toBeNull();
  });

  it('filters songs by instrument when category is multi-instrument', () => {
    const cat = makeCat('near_fc_any', [
      { songId: 's1', title: 'A', artist: 'A', instrumentKey: 'guitar' },
      { songId: 's2', title: 'B', artist: 'B', instrumentKey: 'drums' },
    ]);
    const filter = { ...defaultSuggestionsFilterDraft(), suggestionsLeadNearFC: false };
    const result = filterCategoryForInstrumentTypes(cat, filter);
    expect(result).not.toBeNull();
    expect(result!.songs).toHaveLength(1);
    expect(result!.songs[0]!.songId).toBe('s2');
  });

  it('returns null when all songs are filtered out', () => {
    const cat = makeCat('near_fc_any', [
      { songId: 's1', title: 'A', artist: 'A', instrumentKey: 'guitar' },
    ]);
    const filter = { ...defaultSuggestionsFilterDraft(), suggestionsLeadNearFC: false };
    const result = filterCategoryForInstrumentTypes(cat, filter);
    expect(result).toBeNull();
  });

  it('returns original category when no songs are filtered', () => {
    const cat = makeCat('near_fc_any', [
      { songId: 's1', title: 'A', artist: 'A', instrumentKey: 'guitar' },
      { songId: 's2', title: 'B', artist: 'B', instrumentKey: 'drums' },
    ]);
    const filter = defaultSuggestionsFilterDraft();
    const result = filterCategoryForInstrumentTypes(cat, filter);
    expect(result).toBe(cat); // same reference = no filtering happened
  });

  it('keeps songs without instrumentKey', () => {
    const cat = makeCat('near_fc_any', [
      { songId: 's1', title: 'A', artist: 'A' }, // no instrumentKey
    ]);
    const filter = { ...defaultSuggestionsFilterDraft(), suggestionsLeadNearFC: false };
    const result = filterCategoryForInstrumentTypes(cat, filter);
    expect(result).toBe(cat);
  });

  it('defaults to showing when per-instrument key is missing from filter', () => {
    const cat = makeCat('unfc_guitar', [{ songId: 's1', title: 'A', artist: 'A' }]);
    // Provide a sparse filter missing the specific instrument key
    const filter = { suggestionsLeadFilter: true } as any;
    const result = filterCategoryForInstrumentTypes(cat, filter);
    expect(result).toBe(cat);
  });

  it('defaults to showing songs when per-instrument key is missing in multi-instrument cat', () => {
    const cat = makeCat('near_fc_any', [
      { songId: 's1', title: 'A', artist: 'A', instrumentKey: 'guitar' },
    ]);
    const filter = { suggestionsLeadFilter: true } as any;
    const result = filterCategoryForInstrumentTypes(cat, filter);
    expect(result).toBe(cat);
  });
});

// ── computeEffectiveSeason ──

describe('computeEffectiveSeason', () => {
  it('returns currentSeason when > 0', () => {
    expect(computeEffectiveSeason(5, null)).toBe(5);
  });

  it('returns 0 when currentSeason is 0 and no player scores', () => {
    expect(computeEffectiveSeason(0, null)).toBe(0);
  });

  it('returns max season from player scores when currentSeason is 0', () => {
    expect(computeEffectiveSeason(0, [
      { season: 3 }, { season: 5 }, { season: 2 },
    ])).toBe(5);
  });

  it('skips null seasons in player scores', () => {
    expect(computeEffectiveSeason(0, [
      { season: null }, { season: 4 }, { season: undefined },
    ])).toBe(4);
  });

  it('returns 0 when all seasons are null', () => {
    expect(computeEffectiveSeason(0, [
      { season: null }, { season: null },
    ])).toBe(0);
  });

  it('returns 0 for empty scores array', () => {
    expect(computeEffectiveSeason(0, [])).toBe(0);
  });
});

// ── getCardDelay ──

describe('getCardDelay', () => {
  it('returns -1 when skipAnim is true', () => {
    expect(getCardDelay(0, true, 'contentIn', 0)).toBe(-1);
  });

  it('returns null when phase is not contentIn', () => {
    expect(getCardDelay(0, false, 'spinner', 0)).toBeNull();
  });

  it('returns -1 for already-revealed cards', () => {
    expect(getCardDelay(2, false, 'contentIn', 5)).toBe(-1);
  });

  it('returns staggered delay for new cards within viewport', () => {
    const delay = getCardDelay(0, false, 'contentIn', 0);
    expect(delay).toBe(0);
    const delay2 = getCardDelay(1, false, 'contentIn', 0);
    expect(delay2).toBe(125);
  });

  it('returns -1 for cards beyond estimated viewport', () => {
    // estimateVisibleCount(200) in test env should be small
    const delay = getCardDelay(1000, false, 'contentIn', 0);
    expect(delay).toBe(-1);
  });
});

// ── buildAlbumArtMap ──

describe('buildAlbumArtMap', () => {
  it('builds map from songs with albumArt', () => {
    const map = buildAlbumArtMap([
      { songId: 's1', albumArt: 'http://example.com/a.jpg' },
      { songId: 's2' },
      { songId: 's3', albumArt: 'http://example.com/b.jpg' },
    ]);
    expect(map.size).toBe(2);
    expect(map.get('s1')).toBe('http://example.com/a.jpg');
    expect(map.has('s2')).toBe(false);
  });

  it('returns empty map for empty songs', () => {
    const map = buildAlbumArtMap([]);
    expect(map.size).toBe(0);
  });
});
