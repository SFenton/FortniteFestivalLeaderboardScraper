import type { InstrumentKey } from './instruments';
import type { Song } from './models';

export const UNAVAILABLE_DIFFICULTY = 99;

export function isChartedDifficulty(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 && value !== UNAVAILABLE_DIFFICULTY;
}

export function getSongInstrumentDifficulty(song: Song, instrument: InstrumentKey): number | undefined {
  const intensities = song.track.in ?? {};

  switch (instrument) {
    case 'guitar':
      return isChartedDifficulty(intensities.gr) ? intensities.gr : undefined;
    case 'bass':
      return isChartedDifficulty(intensities.ba) ? intensities.ba : undefined;
    case 'drums':
      return isChartedDifficulty(intensities.ds) ? intensities.ds : undefined;
    case 'vocals':
      return isChartedDifficulty(intensities.vl) ? intensities.vl : undefined;
    case 'pro_guitar':
      return isChartedDifficulty(intensities.pg)
        ? intensities.pg
        : isChartedDifficulty(intensities.gr)
          ? intensities.gr
          : undefined;
    case 'pro_bass':
      return isChartedDifficulty(intensities.pb)
        ? intensities.pb
        : isChartedDifficulty(intensities.ba)
          ? intensities.ba
          : undefined;
    case 'peripheral_vocals':
      return isChartedDifficulty(intensities.bd) ? intensities.bd : undefined;
    case 'peripheral_cymbals':
    case 'peripheral_drums':
      return isChartedDifficulty(intensities.pd) ? intensities.pd : undefined;
    default:
      return undefined;
  }
}

export function songSupportsInstrument(song: Song, instrument: InstrumentKey): boolean {
  return getSongInstrumentDifficulty(song, instrument) != null;
}