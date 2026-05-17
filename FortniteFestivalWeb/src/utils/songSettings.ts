/**
 * Song list sort/filter types and defaults.
 * These parallel the @festival/core songListConfig types but use the
 * ServerInstrumentKey from the web models layer.
 */
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import { INSTRUMENT_KEYS } from '@festival/core/api/serverTypes';

/* ── Sort ── */

export type BandIntensitySortMode = `bandIntensity:${InstrumentKey}`;

export type SongSortMode =
  | 'title'
  | 'artist'
  | 'year'
  | 'duration'
  | 'shop'
  | 'hasfc'
  | 'lastplayed'
  | 'score'
  | 'percentage'
  | 'percentile'
  | 'stars'
  | 'seasonachieved'
  | 'intensity'
  | 'difficulty'
  | 'maxdistance'
  | 'maxscorediff'
  | BandIntensitySortMode;

const BAND_INTENSITY_SORT_PREFIX = 'bandIntensity:';

/** Sort modes only valid when an instrument filter is active. */
export const INSTRUMENT_SORT_MODES: { mode: SongSortMode; label: string }[] = [
  { mode: 'score', label: 'Score' },
  { mode: 'percentage', label: 'Percentage' },
  { mode: 'percentile', label: 'Percentile' },
  { mode: 'stars', label: 'Stars' },
  { mode: 'seasonachieved', label: 'Season' },
  { mode: 'intensity', label: 'Intensity' },
  { mode: 'difficulty', label: 'Difficulty' },
  { mode: 'maxdistance', label: 'Max Score %' },
  { mode: 'maxscorediff', label: 'Max Score Diff' },
];

export const isInstrumentSortMode = (mode: SongSortMode): boolean =>
  INSTRUMENT_SORT_MODES.some(m => m.mode === mode);

export const parseBandIntensityInstrument = (mode: SongSortMode | string | null | undefined): InstrumentKey | null => {
  if (typeof mode !== 'string' || !mode.startsWith(BAND_INTENSITY_SORT_PREFIX)) return null;
  const instrument = mode.slice(BAND_INTENSITY_SORT_PREFIX.length);
  return (INSTRUMENT_KEYS as readonly string[]).includes(instrument) ? instrument as InstrumentKey : null;
};

export const isBandIntensitySortMode = (mode: SongSortMode | string | null | undefined): mode is BandIntensitySortMode =>
  parseBandIntensityInstrument(mode) != null;

export const bandIntensitySortMode = (instrument: InstrumentKey): BandIntensitySortMode =>
  `${BAND_INTENSITY_SORT_PREFIX}${instrument}` as BandIntensitySortMode;

export const METADATA_SORT_DISPLAY: Record<string, string> = {
  score: 'Score',
  percentage: 'Percentage',
  percentile: 'Percentile',
  stars: 'Stars',
  seasonachieved: 'Season Achieved',
  intensity: 'Song Intensity',
  difficulty: 'Difficulty',
  lastplayed: 'Last Played',
};

export const DEFAULT_METADATA_ORDER: string[] = [
  'score', 'percentage', 'percentile', 'stars', 'seasonachieved', 'intensity', 'difficulty', 'lastplayed',
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
  selectedBandHasScore: boolean;
  selectedBandMissingScore: boolean;
  individualBandMemberScoreFilters: Record<string, IndividualBandMemberScoreFilter>;
  seasonFilter: Record<number, boolean>;
  percentileFilter: Record<number, boolean>;
  starsFilter: Record<number, boolean>;
  difficultyFilter: Record<number, boolean>;
  shopInShop: boolean;
  shopLeavingTomorrow: boolean;
};

export type IndividualBandMemberScoreFilter = {
  hasScore?: boolean;
  missingScore?: boolean;
};

export const defaultSongFilters = (): SongFilters => ({
  missingScores: {},
  missingFCs: {},
  hasScores: {},
  hasFCs: {},
  overThreshold: {},
  selectedBandHasScore: false,
  selectedBandMissingScore: false,
  individualBandMemberScoreFilters: {},
  seasonFilter: {},
  percentileFilter: {},
  starsFilter: {},
  difficultyFilter: {},
  shopInShop: false,
  shopLeavingTomorrow: false,
});

const scopedFilterRecord = (map: Record<string, boolean> | undefined, visibleSet: ReadonlySet<string> | null): Record<string, boolean> => {
  if (!visibleSet) return map ?? {};

  const filtered: Record<string, boolean> = {};
  for (const [key, value] of Object.entries(map ?? {})) {
    if (visibleSet.has(key)) filtered[key] = value;
  }
  return filtered;
};

export const sanitizeSongFiltersForInstruments = (f: SongFilters, visibleInstruments?: readonly InstrumentKey[] | null): SongFilters => {
  if (!visibleInstruments) return f;

  const visibleSet = new Set<string>(visibleInstruments);
  return {
    ...f,
    missingScores: scopedFilterRecord(f.missingScores, visibleSet),
    missingFCs: scopedFilterRecord(f.missingFCs, visibleSet),
    hasScores: scopedFilterRecord(f.hasScores, visibleSet),
    hasFCs: scopedFilterRecord(f.hasFCs, visibleSet),
    overThreshold: scopedFilterRecord(f.overThreshold, visibleSet),
  };
};

export const isVisibleInstrumentFilter = (instrument: InstrumentKey | null | undefined, visibleInstruments?: readonly InstrumentKey[] | null): instrument is InstrumentKey => {
  if (!instrument) return false;
  return !visibleInstruments || visibleInstruments.includes(instrument);
};

export const isFilterActive = (f: SongFilters, instrument?: InstrumentKey | null, shopVisible?: boolean, visibleInstruments?: readonly InstrumentKey[] | null, selectedBandMode = false): boolean => {
  if (shopVisible && (f.shopInShop || f.shopLeavingTomorrow)) return true;
  if (selectedBandMode) return f.selectedBandHasScore || f.selectedBandMissingScore || hasIndividualBandMemberScoreFilters(f);
  const scoped = sanitizeSongFiltersForInstruments(f, visibleInstruments);
  const hasPerInstrument =
    Object.values(scoped.missingScores).some(v => v === true) ||
    Object.values(scoped.missingFCs).some(v => v === true) ||
    Object.values(scoped.hasScores).some(v => v === true) ||
    Object.values(scoped.hasFCs).some(v => v === true) ||
    Object.values(scoped.overThreshold ?? {}).some(v => v === true);
  if (hasPerInstrument) return true;
  // When instrument is explicitly null, ignore instrument-dependent filters
  // (season/percentile/stars/difficulty) since they won't be applied.
  if (instrument === null || (visibleInstruments && !isVisibleInstrumentFilter(instrument, visibleInstruments))) return false;
  return (
    Object.values(f.seasonFilter).some(v => v === false) ||
    Object.values(f.percentileFilter).some(v => v === false) ||
    Object.values(f.starsFilter).some(v => v === false) ||
    Object.values(f.difficultyFilter).some(v => v === false)
  );
};

export const hasIndividualBandMemberScoreFilters = (f: SongFilters): boolean =>
  Object.values(f.individualBandMemberScoreFilters ?? {}).some(filter => !!filter?.hasScore || !!filter?.missingScore);

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

export function normalizeSongSettings(settings: SongSettings): SongSettings {
  if (settings.instrument !== null || !isInstrumentSortMode(settings.sortMode)) {
    return settings;
  }

  const defaults = defaultSongSettings();
  return {
    ...settings,
    sortMode: defaults.sortMode,
    sortAscending: defaults.sortAscending,
  };
}

export function loadSongSettings(): SongSettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return defaultSongSettings();
    const parsed = JSON.parse(raw);
    // Merge with defaults to handle missing keys from older versions
    const defaults = defaultSongSettings();
    return normalizeSongSettings({
      sortMode: parsed.sortMode ?? defaults.sortMode,
      sortAscending: parsed.sortAscending ?? defaults.sortAscending,
      metadataOrder: migrateMetadataOrder(parsed.metadataOrder ?? defaults.metadataOrder),
      instrumentOrder: parsed.instrumentOrder ?? defaults.instrumentOrder,
      filters: {
        ...defaults.filters,
        ...(parsed.filters ?? {}),
      },
      instrument: parsed.instrument ?? defaults.instrument,
    });
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
  const updated = normalizeSongSettings({
    ...current,
    filters: defaults.filters,
    instrument: defaults.instrument,
  });
  saveSongSettings(updated);
}
