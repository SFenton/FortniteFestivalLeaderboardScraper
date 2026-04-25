import type {Song} from '../models';
import {getSongInstrumentDifficulty, isChartedDifficulty, songSupportsInstrument} from '../songAvailability';

const mkSong = (bd?: number): Song => ({
  track: {
    su: 'song-1',
    in: {
      gr: 3,
      bd,
    },
  },
});

describe('songAvailability', () => {
  test('treats 99 as an absent Karaoke chart', () => {
    const song = mkSong(99);

    expect(isChartedDifficulty(99)).toBe(false);
    expect(songSupportsInstrument(song, 'peripheral_vocals')).toBe(false);
    expect(getSongInstrumentDifficulty(song, 'peripheral_vocals')).toBeUndefined();
  });

  test('keeps valid Karaoke difficulties available', () => {
    const song = mkSong(0);

    expect(isChartedDifficulty(0)).toBe(true);
    expect(songSupportsInstrument(song, 'peripheral_vocals')).toBe(true);
    expect(getSongInstrumentDifficulty(song, 'peripheral_vocals')).toBe(0);
  });
});