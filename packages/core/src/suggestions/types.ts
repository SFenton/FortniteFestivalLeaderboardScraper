import type {InstrumentKey} from '../instruments';

export type SuggestionSongItem = {
  songId: string;
  title: string;
  artist: string;
  stars?: number;
  percent?: number;
  fullCombo?: boolean;
  instrumentKey?: InstrumentKey;
  percentileDisplay?: string;
};

export type SuggestionCategory = {
  key: string;
  title: string;
  description: string;
  songs: SuggestionSongItem[];
};
