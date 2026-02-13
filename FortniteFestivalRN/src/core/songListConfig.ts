import type {InstrumentKey} from './instruments';

/** Bucket thresholds used for leaderboard percentile display ("Top N%"). */
export const PERCENTILE_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;

/**
 * Map a raw percentile fraction (e.g. 0.0144) to its display bucket (e.g. 2).
 * Returns 0 when the tracker has no percentile data.
 */
export const percentileBucket = (rawPercentile: number): number => {
  if (rawPercentile <= 0) return 0;
  let topPct = rawPercentile * 100;
  if (topPct > 100) topPct = 100;
  if (topPct < 1) topPct = 1;
  return PERCENTILE_THRESHOLDS.find(t => topPct <= t) ?? 100;
};

export type SongSortMode = 'title' | 'artist' | 'year' | 'hasfc' | 'isfc' | 'score' | 'percentage' | 'percentile' | 'stars' | 'seasonachieved';

/** Sort modes that require an active instrument filter to be meaningful. */
export const instrumentSortModes: ReadonlyArray<SongSortMode> = ['score', 'percentage', 'percentile', 'isfc', 'stars', 'seasonachieved'];

export const isInstrumentSortMode = (mode: SongSortMode): boolean =>
  (instrumentSortModes as ReadonlyArray<string>).includes(mode);

export type AdvancedMissingFilters = {
  missingPadFCs: boolean;
  missingProFCs: boolean;
  missingPadScores: boolean;
  missingProScores: boolean;
  includeLead: boolean;
  includeBass: boolean;
  includeDrums: boolean;
  includeVocals: boolean;
  includeProGuitar: boolean;
  includeProBass: boolean;
  /**
   * Per-season visibility filter.
   * Key = season number (0 = "No Season"). Value = whether that season is shown.
   * An empty object means "show all seasons" (default).
   */
  seasonFilter: Record<number, boolean>;
  /**
   * Per-percentile-bucket visibility filter.
   * Key = bucket value from PERCENTILE_THRESHOLDS (0 = "No Percentile").
   * An empty object means "show all" (default); explicit false = hidden.
   */
  percentileFilter: Record<number, boolean>;
  /**
   * Per-star-count visibility filter.
   * Key = number of stars (0 = "No Stars", 1–5 = star counts, 6 = gold stars).
   * An empty object means "show all" (default); explicit false = hidden.
   */
  starsFilter: Record<number, boolean>;
};

export const defaultAdvancedMissingFilters = (): AdvancedMissingFilters => ({
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
});

// ── Metadata sort priority (instrument-specific views) ──

export type MetadataSortKey = 'title' | 'artist' | 'year' | 'score' | 'percentage' | 'percentile' | 'isfc' | 'stars' | 'seasonachieved';

export type MetadataSortItem = {key: MetadataSortKey; displayName: string};

export const defaultMetadataSortPriority = (): MetadataSortItem[] => [
  {key: 'title', displayName: 'Title'},
  {key: 'artist', displayName: 'Artist'},
  {key: 'year', displayName: 'Year'},
  {key: 'score', displayName: 'Score'},
  {key: 'percentage', displayName: 'Percentage'},
  {key: 'percentile', displayName: 'Percentile'},
  {key: 'isfc', displayName: 'Is FC'},
  {key: 'stars', displayName: 'Stars'},
  {key: 'seasonachieved', displayName: 'Season Achieved'},
];

export const normalizeMetadataSortPriority = (keys: ReadonlyArray<MetadataSortKey> | undefined): MetadataSortItem[] => {
  const base = defaultMetadataSortPriority();
  if (!keys || keys.length === 0) return base;
  const map = new Map<MetadataSortKey, MetadataSortItem>(base.map(i => [i.key, i]));
  const out: MetadataSortItem[] = [];
  for (const k of keys) {
    const it = map.get(k);
    if (it) { out.push(it); map.delete(k); }
  }
  for (const it of map.values()) out.push(it);
  return out;
};

// ── Instrument order ──

export type InstrumentOrderItem = {key: InstrumentKey; displayName: string};

export const defaultPrimaryInstrumentOrder = (): InstrumentOrderItem[] => [
  {key: 'guitar', displayName: 'Lead'},
  {key: 'drums', displayName: 'Drums'},
  {key: 'vocals', displayName: 'Vocals'},
  {key: 'bass', displayName: 'Bass'},
  {key: 'pro_guitar', displayName: 'Pro Lead'},
  {key: 'pro_bass', displayName: 'Pro Bass'},
];

export const normalizeInstrumentOrder = (keys: ReadonlyArray<InstrumentKey> | undefined): InstrumentOrderItem[] => {
  const base = defaultPrimaryInstrumentOrder();
  if (!keys || keys.length === 0) return base;

  const map = new Map<InstrumentKey, InstrumentOrderItem>(base.map(i => [i.key, i]));
  const out: InstrumentOrderItem[] = [];

  for (const k of keys) {
    const it = map.get(k);
    if (it) {
      out.push(it);
      map.delete(k);
    }
  }

  // Append any missing keys (for forward-compat if keys list is stale)
  for (const it of map.values()) out.push(it);

  return out;
};

/* ── Instrument visibility helpers ── */

export type InstrumentShowSettings = {
  showLead: boolean;
  showBass: boolean;
  showDrums: boolean;
  showVocals: boolean;
  showProLead: boolean;
  showProBass: boolean;
};

type ShowSettingKey = keyof InstrumentShowSettings;

/** Map an InstrumentKey to its corresponding `show*` settings key. */
export const showSettingKeyForInstrument = (key: InstrumentKey): ShowSettingKey => {
  switch (key) {
    case 'guitar': return 'showLead';
    case 'bass': return 'showBass';
    case 'drums': return 'showDrums';
    case 'vocals': return 'showVocals';
    case 'pro_guitar': return 'showProLead';
    case 'pro_bass': return 'showProBass';
  }
};

/** Whether an instrument is visible given the current show-instrument settings. */
export const isInstrumentVisible = (key: InstrumentKey, settings: InstrumentShowSettings): boolean =>
  settings[showSettingKeyForInstrument(key)];

// ── Song row visual order (display-only, independent of sort) ──

export type SongRowVisualKey = 'score' | 'percentage' | 'percentile' | 'stars' | 'seasonachieved';

export type SongRowVisualItem = {key: SongRowVisualKey; displayName: string};

export const defaultSongRowVisualOrder = (): SongRowVisualItem[] => [
  {key: 'score', displayName: 'Score'},
  {key: 'percentage', displayName: 'Percent'},
  {key: 'percentile', displayName: 'Percentile'},
  {key: 'stars', displayName: 'Stars'},
  {key: 'seasonachieved', displayName: 'Season Achieved'},
];

export const normalizeSongRowVisualOrder = (keys: ReadonlyArray<SongRowVisualKey> | undefined): SongRowVisualItem[] => {
  const base = defaultSongRowVisualOrder();
  if (!keys || keys.length === 0) return base;
  const map = new Map<SongRowVisualKey, SongRowVisualItem>(base.map(i => [i.key, i]));
  const out: SongRowVisualItem[] = [];
  for (const k of keys) {
    const it = map.get(k);
    if (it) { out.push(it); map.delete(k); }
  }
  for (const it of map.values()) out.push(it);
  return out;
};

/**
 * Reorder the Primary Instrument Order list when an instrument's visibility changes.
 *
 * - **Hiding:** moves the instrument to the end of the list.
 * - **Showing:** re-inserts the instrument at its default-relative position
 *   among the *visible* instruments (i.e. after the last visible preceding
 *   instrument in the default order). `showSettings` should reflect the state
 *   *before* the toggle so that other hidden instruments are correctly skipped.
 */
export const reorderPIOForVisibilityChange = (
  currentOrder: InstrumentKey[],
  changedKey: InstrumentKey,
  isNowVisible: boolean,
  showSettings: InstrumentShowSettings,
): InstrumentKey[] => {
  const without = currentOrder.filter(k => k !== changedKey);

  if (!isNowVisible) {
    // Hidden → push to end
    return [...without, changedKey];
  }

  // Re-enabled → find the default-relative insertion point among visible instruments
  const defaults = defaultPrimaryInstrumentOrder().map(i => i.key);
  const defaultIndex = defaults.indexOf(changedKey);

  // Walk backwards through the default order from changedKey's position and
  // find the last preceding instrument that is visible and exists in `without`.
  let insertAfter = -1;
  for (let i = defaultIndex - 1; i >= 0; i--) {
    const pred = defaults[i];
    // Skip predecessors that are themselves hidden
    if (!showSettings[showSettingKeyForInstrument(pred)]) continue;
    const idx = without.indexOf(pred);
    if (idx !== -1) {
      insertAfter = idx;
      break;
    }
  }

  const result = [...without];
  result.splice(insertAfter + 1, 0, changedKey);
  return result;
};
