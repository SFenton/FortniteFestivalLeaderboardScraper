/**
 * Single source of truth for suggestion type definitions.
 *
 * Adding a new entry to {@link SUGGESTION_TYPES} automatically creates:
 *  - a global toggle  (`suggestionsShow<Id>`)
 *  - per-instrument toggles (`suggestions<InstrumentLabel><Id>`) for every instrument
 *  - the matching defaults (all `true`)
 *  - UI rows in the General and Instrument-Specific filter sections
 *
 * No other file needs to change.
 */
import type {InstrumentKey} from '../instruments';

// ---------------------------------------------------------------------------
// Instrument → setting-name prefix mapping
// ---------------------------------------------------------------------------

/** Maps each InstrumentKey to the label used in setting key names. */
export const INSTRUMENT_SETTING_LABELS = {
  guitar: 'Lead',
  bass: 'Bass',
  drums: 'Drums',
  vocals: 'Vocals',
  pro_guitar: 'ProLead',
  pro_bass: 'ProBass',
} as const satisfies Record<InstrumentKey, string>;

export type InstrumentSettingLabel = (typeof INSTRUMENT_SETTING_LABELS)[InstrumentKey];

// ---------------------------------------------------------------------------
// Suggestion type definitions
// ---------------------------------------------------------------------------

export const SUGGESTION_TYPES = [
  {id: 'NearFC',            label: 'Near FC',            description: "Songs you're close to full-comboing."},
  {id: 'StarProgress',      label: 'Star Progress',      description: 'Push five-star runs to gold, or gain more stars.'},
  {id: 'Unplayed',          label: 'Unplayed',           description: "Songs you haven't played yet."},
  {id: 'VarietyPack',       label: 'Variety Pack',       description: 'A mix of songs from different artists.'},
  {id: 'ArtistEssentials',  label: 'Artist Essentials',  description: 'A selection of songs by a single artist.'},
  {id: 'ArtistDiscover',    label: 'Artist Discover',    description: 'Unplayed songs from a single artist.'},
  {id: 'SameName',          label: 'Same Name',          description: 'Different tracks that share the same title.'},
  {id: 'AlmostElite',       label: 'Almost Elite',       description: 'Top 5% \u2014 one good run could crack the top 1%.'},
  {id: 'PercentilePush',    label: 'Percentile Push',    description: 'Close to the next percentile bracket.'},
  {id: 'Stale',             label: 'Stale Songs',        description: "Songs you haven't played in a while."},
  {id: 'PctImprove',        label: 'Percentile Improve', description: 'Songs with room for percentile improvement.'},
] as const;

export type SuggestionTypeId = (typeof SUGGESTION_TYPES)[number]['id'];

// ---------------------------------------------------------------------------
// Auto-generated setting key types
// ---------------------------------------------------------------------------

/** `suggestionsShowNearFC`, `suggestionsShowStarProgress`, … */
type GlobalSuggestionTypeKey = `suggestionsShow${SuggestionTypeId}`;

/** `suggestionsLeadNearFC`, `suggestionsBassStarProgress`, `suggestionsProLeadVariety`, … */
type PerInstrumentSuggestionTypeKey = `suggestions${InstrumentSettingLabel}${SuggestionTypeId}`;

/** Union of every auto-generated suggestion-filter setting key. */
export type SuggestionTypeSettingsKey = GlobalSuggestionTypeKey | PerInstrumentSuggestionTypeKey;

/** Record mapping every auto-generated key to `boolean`. */
export type SuggestionTypeSettings = Record<SuggestionTypeSettingsKey, boolean>;

// ---------------------------------------------------------------------------
// Defaults
// ---------------------------------------------------------------------------

/** Returns a fresh object with every suggestion-type setting set to `true`. */
export function defaultSuggestionTypeSettings(): SuggestionTypeSettings {
  const result: Record<string, boolean> = {};
  const labels = Object.values(INSTRUMENT_SETTING_LABELS);
  for (const {id} of SUGGESTION_TYPES) {
    result[`suggestionsShow${id}`] = true;
    for (const label of labels) {
      result[`suggestions${label}${id}`] = true;
    }
  }
  return result as SuggestionTypeSettings;
}

// ---------------------------------------------------------------------------
// Key helpers
// ---------------------------------------------------------------------------

/** Build the global-toggle setting key for a suggestion type. */
export const globalKeyFor = (typeId: SuggestionTypeId): GlobalSuggestionTypeKey =>
  `suggestionsShow${typeId}` as GlobalSuggestionTypeKey;

/** Build the per-instrument setting key for a given instrument + type pair. */
export const perInstrumentKeyFor = (instrument: InstrumentKey, typeId: SuggestionTypeId): PerInstrumentSuggestionTypeKey =>
  `suggestions${INSTRUMENT_SETTING_LABELS[instrument]}${typeId}` as PerInstrumentSuggestionTypeKey;

/** The setting-key prefix used for a specific instrument (e.g. `'suggestionsLead'`). */
export const instrumentSettingPrefix = (instrument: InstrumentKey): string =>
  `suggestions${INSTRUMENT_SETTING_LABELS[instrument]}`;

// ---------------------------------------------------------------------------
// Category-key → type-id resolution
// ---------------------------------------------------------------------------

/**
 * Given a suggestion generator category key (e.g. `'unfc_guitar'`, `'variety_pack'`),
 * return the matching {@link SuggestionTypeId}, or `null` for unknown keys.
 */
export function getCategoryTypeId(categoryKey: string): SuggestionTypeId | null {
  const key = categoryKey.toLowerCase();
  // Near FC: near_fc_any, near_fc_relaxed, unfc_*, samename_nearfc_*
  if (key.startsWith('near_fc') || key.startsWith('unfc_') || key.includes('samename_nearfc')) return 'NearFC';
  // Star progress: almost_six_star, star_gains, more_stars
  if (key.startsWith('almost_six_star') || key.startsWith('star_gains') || key.startsWith('more_stars')) return 'StarProgress';
  // Almost elite
  if (key.startsWith('almost_elite')) return 'AlmostElite';
  // Percentile push
  if (key.startsWith('pct_push')) return 'PercentilePush';
  // Stale / untouched songs
  if (key.startsWith('stale_')) return 'Stale';
  // Percentile improvements
  if (key.startsWith('pct_improve') || key.startsWith('same_pct_improve') || key.startsWith('improve_rankings_')) return 'PctImprove';
  // Unplayed: unplayed_*, first_plays_mixed
  if (key.startsWith('unplayed_') || key.startsWith('first_plays_mixed')) return 'Unplayed';
  // Variety pack
  if (key.startsWith('variety_pack')) return 'VarietyPack';
  // Artist essentials: artist_sampler_*
  if (key.startsWith('artist_sampler_')) return 'ArtistEssentials';
  // Artist discover: artist_unplayed_*
  if (key.startsWith('artist_unplayed_')) return 'ArtistDiscover';
  // Same name: samename_* (but not samename_nearfc which is handled above)
  if (key.startsWith('samename_')) return 'SameName';
  return null;
}

/**
 * Given a suggestion generator category key, extract the instrument it targets,
 * or `null` for instrument-agnostic / multi-instrument categories.
 */
const INSTRUMENT_TYPE_PREFIXES = ['unfc_', 'unplayed_', 'almost_elite_', 'pct_push_', 'stale_', 'pct_improve_', 'improve_rankings_'];

export function getCategoryInstrument(categoryKey: string): InstrumentKey | null {
  const k = categoryKey.toLowerCase();
  let remainder: string | null = null;
  for (const p of INSTRUMENT_TYPE_PREFIXES) {
    if (k.startsWith(p)) {
      remainder = k.substring(p.length);
      break;
    }
  }
  if (!remainder) return null;
  if (remainder.startsWith('pro_guitar')) return 'pro_guitar';
  if (remainder.startsWith('pro_bass')) return 'pro_bass';
  if (remainder.startsWith('guitar')) return 'guitar';
  if (remainder.startsWith('bass')) return 'bass';
  if (remainder.startsWith('drums')) return 'drums';
  if (remainder.startsWith('vocals')) return 'vocals';
  return null;
}
