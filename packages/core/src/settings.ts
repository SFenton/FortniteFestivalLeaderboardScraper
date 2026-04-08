import type {InstrumentKey} from './instruments';
import type {AdvancedMissingFilters, MetadataSortKey, SongRowVisualKey, SongSortMode} from './songListConfig';
import {defaultAdvancedMissingFilters, defaultMetadataSortPriority, defaultPrimaryInstrumentOrder, defaultSongRowVisualOrder} from './songListConfig';
import type {SuggestionTypeSettings} from './suggestions/suggestionFilterConfig';
import {defaultSuggestionTypeSettings} from './suggestions/suggestionFilterConfig';

export type Settings = {
  queryLead: boolean;
  queryDrums: boolean;
  queryVocals: boolean;
  queryBass: boolean;
  queryProLead: boolean;
  queryProBass: boolean;

  showLead: boolean;
  showDrums: boolean;
  showVocals: boolean;
  showBass: boolean;
  showProLead: boolean;
  showProBass: boolean;

  // Whether we've ever successfully synced the song catalog at least once.
  hasEverSyncedSongs: boolean;

  // Whether we've ever successfully completed a score sync (even if it yielded 0 scores).
  hasEverSyncedScores: boolean;

  // Songs list preferences
  songsSortMode: SongSortMode;
  songsSortAscending: boolean;
  songsAdvancedMissingFilters: AdvancedMissingFilters;
  songsPrimaryInstrumentOrder: InstrumentKey[];

  // Instrument-specific metadata sort priority order.
  // Determines the cascade order when sorting by instrument-specific properties.
  songMetadataSortPriority: MetadataSortKey[];

  // Whether to use an independent visual order for song row metadata (vs. following sort priority).
  songRowVisualOrderEnabled: boolean;

  // Song row visual display order (independent of sort order).
  // Controls the order metadata items appear in on filtered-instrument song rows.
  songRowVisualOrder: SongRowVisualKey[];

  // Suggestions page instrument filters (separate from global show* toggles)
  suggestionsLeadFilter: boolean;
  suggestionsBassFilter: boolean;
  suggestionsDrumsFilter: boolean;
  suggestionsVocalsFilter: boolean;
  suggestionsProLeadFilter: boolean;
  suggestionsProBassFilter: boolean;

  // Songs list instrument-specific filter.
  // When set, only shows metadata for the selected instrument on each song row.
  songsSelectedInstrumentFilter: InstrumentKey | null;

  // Songs list compact layout visibility toggles
  // When true, hides instrument icons (metadata-only row).
  songsHideInstrumentIcons: boolean;

  // Instrument metadata visibility toggles.
  // When filtering to a single instrument, these control which metadata pieces appear on each song row.
  metadataShowScore: boolean;
  metadataShowPercentage: boolean;
  metadataShowPercentile: boolean;
  metadataShowSeasonAchieved: boolean;
  metadataShowIntensity: boolean;
  metadataShowGameDifficulty: boolean;
  metadataShowIsFC: boolean;
  metadataShowStars: boolean;

  // iOS-specific settings (only relevant on iOS 26+)
  /** Whether liquid glass effect is enabled (iOS 26+ only). */
  iosLiquidGlassEnabled: boolean;
  /** Liquid glass style: 'none' | 'regular' | 'clear' (iOS 26+ only). */
  iosLiquidGlassStyle: 'none' | 'regular' | 'clear';
  /** Whether blur effect is enabled on surfaces. Disabled when liquid glass is active. */
  iosBlurEnabled: boolean;
} & SuggestionTypeSettings;

export const defaultSettings = (): Settings => ({
  queryLead: true,
  queryDrums: true,
  queryVocals: true,
  queryBass: true,
  queryProLead: true,
  queryProBass: true,

  showLead: true,
  showDrums: true,
  showVocals: true,
  showBass: true,
  showProLead: true,
  showProBass: true,

  hasEverSyncedSongs: false,

  hasEverSyncedScores: false,

  songsSortMode: 'title',
  songsSortAscending: true,
  songsAdvancedMissingFilters: defaultAdvancedMissingFilters(),
  songsPrimaryInstrumentOrder: defaultPrimaryInstrumentOrder().map(i => i.key),
  songMetadataSortPriority: defaultMetadataSortPriority().map(i => i.key),
  songRowVisualOrderEnabled: false,
  songRowVisualOrder: defaultSongRowVisualOrder().map(i => i.key),

  suggestionsLeadFilter: true,
  suggestionsBassFilter: true,
  suggestionsDrumsFilter: true,
  suggestionsVocalsFilter: true,
  suggestionsProLeadFilter: true,
  suggestionsProBassFilter: true,

  ...defaultSuggestionTypeSettings(),

  songsSelectedInstrumentFilter: null,

  songsHideInstrumentIcons: false,

  metadataShowScore: true,
  metadataShowPercentage: true,
  metadataShowPercentile: true,
  metadataShowSeasonAchieved: true,
  metadataShowIntensity: true,
  metadataShowGameDifficulty: true,
  metadataShowIsFC: true,
  metadataShowStars: true,

  iosLiquidGlassEnabled: true,
  iosLiquidGlassStyle: 'regular',
  iosBlurEnabled: true,
});
