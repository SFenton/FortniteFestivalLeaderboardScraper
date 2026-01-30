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

  // Songs list preferences
  songsSortMode: SongSortMode;
  songsSortAscending: boolean;
  songsAdvancedMissingFilters: AdvancedMissingFilters;
  songsPrimaryInstrumentOrder: InstrumentKey[];
};

export const defaultSettings = (): Settings => ({
  degreeOfParallelism: 16,
  queryLead: true,
  queryDrums: true,
  queryVocals: true,
  queryBass: true,
  queryProLead: true,
  queryProBass: true,

  songsSortMode: 'title',
  songsSortAscending: true,
  songsAdvancedMissingFilters: defaultAdvancedMissingFilters(),
  songsPrimaryInstrumentOrder: defaultPrimaryInstrumentOrder().map(i => i.key),
});
