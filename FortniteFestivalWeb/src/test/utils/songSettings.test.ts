import { describe, it, expect, beforeEach } from 'vitest';
import {
  INSTRUMENT_SORT_MODES,
  isInstrumentSortMode,
  METADATA_SORT_DISPLAY,
  DEFAULT_METADATA_ORDER,
  defaultSongFilters,
  isFilterActive,
  defaultSongSettings,
  loadSongSettings,
  saveSongSettings,
  resetSongSettingsForDeselect,
  SONG_SETTINGS_CHANGED_EVENT,
} from '../../utils/songSettings';

const STORAGE_KEY = 'fst:songSettings';

beforeEach(() => {
  localStorage.clear();
});

describe('songSettings', () => {
  describe('getInstrumentSortModes', () => {
    it('returns 6 instrument sort modes', () => {
      const modes = INSTRUMENT_SORT_MODES;
      expect(modes).toHaveLength(6);
      expect(modes.map(m => m.mode)).toContain('score');
      expect(modes.map(m => m.mode)).toContain('intensity');
    });

    it('each mode has a label', () => {
      for (const m of INSTRUMENT_SORT_MODES) {
        expect(m.label).toBeTruthy();
      }
    });
  });

  describe('isInstrumentSortMode', () => {
    it('returns true for instrument sort modes', () => {
      expect(isInstrumentSortMode('score')).toBe(true);
      expect(isInstrumentSortMode('percentile')).toBe(true);
      expect(isInstrumentSortMode('intensity')).toBe(true);
    });

    it('returns false for non-instrument sort modes', () => {
      expect(isInstrumentSortMode('title')).toBe(false);
      expect(isInstrumentSortMode('artist')).toBe(false);
      expect(isInstrumentSortMode('year')).toBe(false);
    });
  });

  describe('getMetadataSortDisplay', () => {
    it('returns display names for all metadata keys', () => {
      const display = METADATA_SORT_DISPLAY;
      expect(display.score).toBeTruthy();
      expect(display.percentage).toBeTruthy();
      expect(display.percentile).toBeTruthy();
      expect(display.stars).toBeTruthy();
      expect(display.seasonachieved).toBeTruthy();
      expect(display.intensity).toBeTruthy();
    });
  });

  describe('DEFAULT_METADATA_ORDER', () => {
    it('contains 6 keys', () => {
      expect(DEFAULT_METADATA_ORDER).toHaveLength(6);
    });
  });

  describe('defaultSongFilters', () => {
    it('returns empty filter records', () => {
      const f = defaultSongFilters();
      expect(Object.keys(f.missingScores)).toHaveLength(0);
      expect(Object.keys(f.seasonFilter)).toHaveLength(0);
    });
  });

  describe('isFilterActive', () => {
    it('returns false for default filters', () => {
      expect(isFilterActive(defaultSongFilters())).toBe(false);
    });

    it('returns true when a missingScores key is true', () => {
      const f = { ...defaultSongFilters(), missingScores: { Solo_Guitar: true } };
      expect(isFilterActive(f)).toBe(true);
    });

    it('returns true when a seasonFilter key is false', () => {
      const f = { ...defaultSongFilters(), seasonFilter: { 1: false } };
      expect(isFilterActive(f)).toBe(true);
    });

    it('returns false when all explicit values are default-ish', () => {
      const f = { ...defaultSongFilters(), missingScores: { Solo_Guitar: false } };
      expect(isFilterActive(f)).toBe(false);
    });
  });

  describe('defaultSongSettings', () => {
    it('returns title sort ascending', () => {
      const s = defaultSongSettings();
      expect(s.sortMode).toBe('title');
      expect(s.sortAscending).toBe(true);
    });

    it('has null instrument', () => {
      expect(defaultSongSettings().instrument).toBeNull();
    });
  });

  describe('loadSongSettings', () => {
    it('returns defaults when localStorage is empty', () => {
      expect(loadSongSettings()).toEqual(defaultSongSettings());
    });

    it('returns defaults on invalid JSON', () => {
      localStorage.setItem(STORAGE_KEY, 'not json');
      expect(loadSongSettings()).toEqual(defaultSongSettings());
    });

    it('merges partial saved settings with defaults', () => {
      localStorage.setItem(STORAGE_KEY, JSON.stringify({ sortMode: 'artist' }));
      const loaded = loadSongSettings();
      expect(loaded.sortMode).toBe('artist');
      expect(loaded.sortAscending).toBe(true); // from defaults
    });
  });

  describe('saveSongSettings', () => {
    it('persists to localStorage', () => {
      const settings = { ...defaultSongSettings(), sortMode: 'year' as const };
      saveSongSettings(settings);
      const raw = localStorage.getItem(STORAGE_KEY);
      expect(raw).toBeTruthy();
      expect(JSON.parse(raw!).sortMode).toBe('year');
    });

    it('dispatches SONG_SETTINGS_CHANGED_EVENT', () => {
      let fired = false;
      const handler = () => { fired = true; };
      window.addEventListener(SONG_SETTINGS_CHANGED_EVENT, handler);
      saveSongSettings(defaultSongSettings());
      window.removeEventListener(SONG_SETTINGS_CHANGED_EVENT, handler);
      expect(fired).toBe(true);
    });
  });

  describe('resetSongSettingsForDeselect', () => {
    it('resets filters and instrument but keeps non-instrument sort mode', () => {
      saveSongSettings({
        ...defaultSongSettings(),
        sortMode: 'artist',
        instrument: 'Solo_Guitar' as any,
        filters: { ...defaultSongFilters(), missingScores: { Solo_Guitar: true } },
      });

      resetSongSettingsForDeselect();
      const loaded = loadSongSettings();
      expect(loaded.instrument).toBeNull();
      expect(loaded.sortMode).toBe('artist'); // preserved
      expect(isFilterActive(loaded.filters)).toBe(false);
    });

    it('reverts instrument sort mode to title', () => {
      saveSongSettings({
        ...defaultSongSettings(),
        sortMode: 'score',
        instrument: 'Solo_Guitar' as any,
      });

      resetSongSettingsForDeselect();
      const loaded = loadSongSettings();
      expect(loaded.sortMode).toBe('title');
    });
  });

  describe('isFilterActive — all branch paths', () => {
    it('returns true when hasScores has a true value', () => {
      const f = { ...defaultSongFilters(), hasScores: { Solo_Guitar: true } };
      expect(isFilterActive(f)).toBe(true);
    });

    it('returns true when hasFCs has a true value', () => {
      const f = { ...defaultSongFilters(), hasFCs: { Solo_Bass: true } };
      expect(isFilterActive(f)).toBe(true);
    });

    it('returns true when percentileFilter has a false value', () => {
      const f = { ...defaultSongFilters(), percentileFilter: { 1: false } };
      expect(isFilterActive(f)).toBe(true);
    });

    it('returns true when starsFilter has a false value', () => {
      const f = { ...defaultSongFilters(), starsFilter: { 6: false } };
      expect(isFilterActive(f)).toBe(true);
    });

    it('returns true when difficultyFilter has a false value', () => {
      const f = { ...defaultSongFilters(), difficultyFilter: { 3: false } };
      expect(isFilterActive(f)).toBe(true);
    });

    it('returns true when missingFCs has a true value', () => {
      const f = { ...defaultSongFilters(), missingFCs: { Solo_Guitar: true } };
      expect(isFilterActive(f)).toBe(true);
    });

    it('returns false when all filter records only have false/true defaults', () => {
      const f = {
        ...defaultSongFilters(),
        hasScores: { Solo_Guitar: false },
        hasFCs: { Solo_Bass: false },
        percentileFilter: { 1: true },
        starsFilter: { 6: true },
        difficultyFilter: { 3: true },
      };
      expect(isFilterActive(f)).toBe(false);
    });
  });
});
