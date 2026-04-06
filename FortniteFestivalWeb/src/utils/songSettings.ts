/**
 * Song list sort/filter types and defaults.
 * These parallel the @festival/core songListConfig types but use the
 * ServerInstrumentKey from the web models layer.
 */
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { INSTRUMENT_KEYS } from '@festival/core/api/serverTypes';

/* ── Sort ── */

export type SongSortMode =
  | 'title'
  | 'artist'
  | 'year'
  | 'shop'
  | 'hasfc'
  | 'lastplayed'
  | 'score'
  | 'percentage'
  | 'percentile'
  | 'stars'
  | 'seasonachieved'
  | 'intensity'
  | 'maxdistance'
  | 'maxscorediff';

/** Sort modes only valid when an instrument filter is active. */
export const INSTRUMENT_SORT_MODES: { mode: SongSortMode; label: string }[] = [
  { mode: 'score', label: 'Score' },
  { mode: 'percentage', label: 'Percentage' },
  { mode: 'percentile', label: 'Percentile' },
  { mode: 'stars', label: 'Stars' },
  { mode: 'seasonachieved', label: 'Season' },
  { mode: 'intensity', label: 'Intensity' },
  { mode: 'maxdistance', label: 'Max Score %' },
  { mode: 'maxscorediff', label: 'Max Score Diff' },
];

export const isInstrumentSortMode = (mode: SongSortMode): boolean =>
  INSTRUMENT_SORT_MODES.some(m => m.mode === mode);

export const METADATA_SORT_DISPLAY: Record<string, string> = {
  score: 'Score',
  percentage: 'Percentage',
  percentile: 'Percentile',
  stars: 'Stars',
  seasonachieved: 'Season Achieved',
  intensity: 'Song Intensity',
  lastplayed: 'Last Played',
};

export const DEFAULT_METADATA_ORDER: string[] = [
  'score', 'percentage', 'percentile', 'stars', 'seasonachieved', 'intensity', 'lastplayed',
];

/** Append any new DEFAULT_METADATA_ORDER keys missing from a saved order and strip removed keys. */
function migrateMetadataOrder(saved: string[]): string[] {
  const allowed = new Set(DEFAULT_METADATA_ORDER);
  const stripped = saved.filter(k => allowed.has(k));
  const missing = DEFAULT_METADATA_ORDER.filter(k => !stripped.includes(k));
  return missing.length > 0 ? [...stripped, ...missing] : stripped;
}

/* ── Filter ── */

export type SongFilters = {
  missingScores: Record<string, boolean>;
  missingFCs: Record<string, boolean>;
  hasScores: Record<string, boolean>;
  hasFCs: Record<string, boolean>;
  overThreshold: Record<string, boolean>;
  seasonFilter: Record<number, boolean>;
  percentileFilter: Record<number, boolean>;
  starsFilter: Record<number, boolean>;
  difficultyFilter: Record<number, boolean>;
  shopInShop: boolean;
  shopLeavingTomorrow: boolean;
};

export const defaultSongFilters = (): SongFilters => ({
  missingScores: {},
  missingFCs: {},
  hasScores: {},
  hasFCs: {},
  overThreshold: {},
  seasonFilter: {},
  percentileFilter: {},
  starsFilter: {},
  difficultyFilter: {},
  shopInShop: false,
  shopLeavingTomorrow: false,
});

export const isFilterActive = (f: SongFilters, instrument?: InstrumentKey | null, shopVisible?: boolean): boolean => {
  if (shopVisible && (f.shopInShop || f.shopLeavingTomorrow)) return true;
  const hasPerInstrument =
    Object.values(f.missingScores).some(v => v === true) ||
    Object.values(f.missingFCs).some(v => v === true) ||
    Object.values(f.hasScores).some(v => v === true) ||
    Object.values(f.hasFCs).some(v => v === true) ||
    Object.values(f.overThreshold ?? {}).some(v => v === true);
  if (hasPerInstrument) return true;
  // When instrument is explicitly null, ignore instrument-dependent filters
  // (season/percentile/stars/difficulty) since they won't be applied.
  if (instrument === null) return false;
  return (
    Object.values(f.seasonFilter).some(v => v === false) ||
    Object.values(f.percentileFilter).some(v => v === false) ||
    Object.values(f.starsFilter).some(v => v === false) ||
    Object.values(f.difficultyFilter).some(v => v === false)
  );
};

/* ── Persistence ── */

const STORAGE_KEY = 'fst:songSettings';

export type SongSettings = {
  sortMode: SongSortMode;
  sortAscending: boolean;
  metadataOrder: string[];
  instrumentOrder: InstrumentKey[];
  filters: SongFilters;
  instrument: InstrumentKey | null;
};

export const defaultSongSettings = (): SongSettings => ({
  sortMode: 'title',
  sortAscending: true,
  metadataOrder: [...DEFAULT_METADATA_ORDER],
  instrumentOrder: [...INSTRUMENT_KEYS],
  filters: defaultSongFilters(),
  instrument: null,
});

export function loadSongSettings(): SongSettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return defaultSongSettings();
    const parsed = JSON.parse(raw);
    // Merge with defaults to handle missing keys from older versions
    const defaults = defaultSongSettings();
    return {
      sortMode: parsed.sortMode ?? defaults.sortMode,
      sortAscending: parsed.sortAscending ?? defaults.sortAscending,
      metadataOrder: migrateMetadataOrder(parsed.metadataOrder ?? defaults.metadataOrder),
      instrumentOrder: parsed.instrumentOrder ?? defaults.instrumentOrder,
      filters: {
        ...defaults.filters,
        ...(parsed.filters ?? {}),
      },
      instrument: parsed.instrument ?? defaults.instrument,
    };
  } catch {
    return defaultSongSettings();
  }
}

export const SONG_SETTINGS_CHANGED_EVENT = 'fst:songSettingsChanged';

export function saveSongSettings(settings: SongSettings): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
  window.dispatchEvent(new Event(SONG_SETTINGS_CHANGED_EVENT));
}

/** Reset filters and instrument; revert sort to 'title' if an instrument sort mode was active. */
export function resetSongSettingsForDeselect(): void {
  const current = loadSongSettings();
  const defaults = defaultSongSettings();
  const updated: SongSettings = {
    ...current,
    filters: defaults.filters,
    instrument: defaults.instrument,
    sortMode: isInstrumentSortMode(current.sortMode) ? defaults.sortMode : current.sortMode,
    sortAscending: isInstrumentSortMode(current.sortMode) ? defaults.sortAscending : current.sortAscending,
  };
  saveSongSettings(updated);
}
