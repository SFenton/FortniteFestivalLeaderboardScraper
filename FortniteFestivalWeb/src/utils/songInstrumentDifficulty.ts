import type { ServerInstrumentKey as InstrumentKey, ServerSong as Song } from '@festival/core/api/serverTypes';

export const SONG_INSTRUMENT_DIFFICULTY_KEY: Record<InstrumentKey, keyof NonNullable<Song['difficulty']>> = {
  Solo_Guitar: 'guitar',
  Solo_Bass: 'bass',
  Solo_Drums: 'drums',
  Solo_Vocals: 'vocals',
  Solo_PeripheralGuitar: 'proGuitar',
  Solo_PeripheralBass: 'proBass',
  Solo_PeripheralVocals: 'vocals',
  Solo_PeripheralCymbals: 'drums',
  Solo_PeripheralDrums: 'drums',
};

export function getSongInstrumentDifficulty(song: Song, instrument: InstrumentKey | null | undefined): number | undefined {
  if (!instrument) return undefined;

  const difficultyKey = SONG_INSTRUMENT_DIFFICULTY_KEY[instrument];
  return song.difficulty?.[difficultyKey];
}