import type {InstrumentKey} from './instruments';
import type {AdvancedMissingFilters, SongSortMode} from './songListConfig';
import {defaultAdvancedMissingFilters, defaultPrimaryInstrumentOrder} from './songListConfig';

export type Settings = {
  degreeOfParallelism: number;
  queryLead: boolean;
  queryDrums: boolean;
  queryVocals: boolean;
  queryBass: boolean;
  queryProLead: boolean;
  queryProBass: boolean;

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
};

export const defaultSettings = (): Settings => ({
  degreeOfParallelism: 16,
  queryLead: true,
  queryDrums: true,
  queryVocals: true,
  queryBass: true,
  queryProLead: true,
  queryProBass: true,

  hasEverSyncedSongs: false,

  hasEverSyncedScores: false,

  songsSortMode: 'title',
  songsSortAscending: true,
  songsAdvancedMissingFilters: defaultAdvancedMissingFilters(),
  songsPrimaryInstrumentOrder: defaultPrimaryInstrumentOrder().map(i => i.key),

  songsHideInstrumentIcons: false,
});
