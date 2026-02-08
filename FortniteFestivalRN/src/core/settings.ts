import type {InstrumentKey} from './instruments';
import type {AdvancedMissingFilters, SongSortMode} from './songListConfig';
import {defaultAdvancedMissingFilters, defaultPrimaryInstrumentOrder} from './songListConfig';

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

  // Songs list compact layout visibility toggles
  // When true, hides instrument icons (metadata-only row).
  songsHideInstrumentIcons: boolean;

  // iOS-specific settings (only relevant on iOS 26+)
  /** Whether liquid glass effect is enabled (iOS 26+ only). */
  iosLiquidGlassEnabled: boolean;
  /** Liquid glass style: 'none' | 'regular' | 'clear' (iOS 26+ only). */
  iosLiquidGlassStyle: 'none' | 'regular' | 'clear';
  /** Whether blur effect is enabled on surfaces. Disabled when liquid glass is active. */
  iosBlurEnabled: boolean;
};

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

  songsHideInstrumentIcons: false,

  iosLiquidGlassEnabled: true,
  iosLiquidGlassStyle: 'regular',
  iosBlurEnabled: true,
});
