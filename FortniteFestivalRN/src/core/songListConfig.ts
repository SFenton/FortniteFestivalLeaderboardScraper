import type {InstrumentKey} from './instruments';

export type SongSortMode = 'title' | 'artist' | 'hasfc' | 'isfc' | 'score' | 'percentage' | 'percentile' | 'stars' | 'seasonachieved';

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
});

// ── Metadata sort priority (instrument-specific views) ──

export type MetadataSortKey = 'title' | 'artist' | 'score' | 'percentage' | 'percentile' | 'isfc' | 'stars' | 'seasonachieved';

export type MetadataSortItem = {key: MetadataSortKey; displayName: string};

export const defaultMetadataSortPriority = (): MetadataSortItem[] => [
  {key: 'title', displayName: 'Title'},
  {key: 'artist', displayName: 'Artist'},
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
