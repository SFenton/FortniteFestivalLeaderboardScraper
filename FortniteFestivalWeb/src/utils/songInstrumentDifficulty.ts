import {
  SERVER_SONG_DIFFICULTY_KEYS,
  getServerSongInstrumentDifficulty,
  serverSongSupportsInstrument,
  type ServerInstrumentKey as InstrumentKey,
  type ServerSong as Song,
} from '@festival/core/api/serverTypes';

export const SONG_INSTRUMENT_DIFFICULTY_KEY = SERVER_SONG_DIFFICULTY_KEYS;

const LEGACY_INSTRUMENT_KEY_MAP = {
  guitar: 'Solo_Guitar',
  bass: 'Solo_Bass',
  drums: 'Solo_Drums',
  vocals: 'Solo_Vocals',
  pro_guitar: 'Solo_PeripheralGuitar',
  pro_bass: 'Solo_PeripheralBass',
  peripheral_vocals: 'Solo_PeripheralVocals',
  peripheral_cymbals: 'Solo_PeripheralCymbals',
  peripheral_drums: 'Solo_PeripheralDrums',
} as const;

function normalizeInstrumentKey(instrument: InstrumentKey | string | null | undefined): InstrumentKey | null {
  if (!instrument) return null;
  return (LEGACY_INSTRUMENT_KEY_MAP[instrument as keyof typeof LEGACY_INSTRUMENT_KEY_MAP] ?? instrument) as InstrumentKey;
}

export function getSongInstrumentDifficulty(song: Song, instrument: InstrumentKey | string | null | undefined): number | undefined {
  return getServerSongInstrumentDifficulty(song, normalizeInstrumentKey(instrument));
}

export function songSupportsInstrument(song: Song, instrument: InstrumentKey | string | null | undefined): boolean {
  return serverSongSupportsInstrument(song, normalizeInstrumentKey(instrument));
}