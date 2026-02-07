import {ScoreTracker} from '../../core/models';
import type {LeaderboardData, Song} from '../../core/models';
import {defaultSettings} from '../../core/settings';
import {buildSongDisplayRow, defaultAdvancedMissingFilters, defaultPrimaryInstrumentOrder, filterAndSortSongs, songMatchesAdvancedMissing} from '../songs/songFiltering';

describe('app/songs/songFiltering', () => {
  const mkSong = (id: string, title: string, artist: string): Song => ({track: {su: id, tt: title, an: artist, in: {}}});

  test('songMatchesAdvancedMissing treats missing entry as missing score', () => {
    const s = mkSong('a', 'A', 'X');
    const filters = {...defaultAdvancedMissingFilters(), missingPadScores: true, includeLead: true};
    expect(songMatchesAdvancedMissing(s, {}, filters)).toBe(true);
  });

  test('songMatchesAdvancedMissing treats missing entry as missing pad FC', () => {
    const s = mkSong('a', 'A', 'X');
    const filters = {...defaultAdvancedMissingFilters(), missingPadFCs: true, includeLead: true};
    expect(songMatchesAdvancedMissing(s, {}, filters)).toBe(true);
  });

  test('songMatchesAdvancedMissing treats missing entry as missing pro FC', () => {
    const s = mkSong('a', 'A', 'X');
    const filters = {...defaultAdvancedMissingFilters(), missingProFCs: true, includeProGuitar: true};
    expect(songMatchesAdvancedMissing(s, {}, filters)).toBe(true);
  });

  test('filterAndSortSongs sorts by hasfc priority then sequential count', () => {
    const s1 = mkSong('a', 'A', 'X');
    const s2 = mkSong('b', 'B', 'Y');

    const fc = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: true});
    const nf = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});

    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: fc, drums: fc, vocals: fc, bass: fc, pro_guitar: fc, pro_bass: fc},
      b: {songId: 'b', guitar: fc, drums: nf, vocals: nf, bass: nf, pro_guitar: nf, pro_bass: nf},
    };

    const out = filterAndSortSongs({
      songs: [s2, s1],
      scoresIndex,
      sortMode: 'hasfc',
      sortAscending: true,
      instrumentOrder: defaultPrimaryInstrumentOrder(),
    });

    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('buildSongDisplayRow populates instrument statuses and applies settings enable flags', () => {
    const s = mkSong('a', 'A', 'X');
    const t = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 10, numStars: 3, isFullCombo: true, percentHit: 1000000});
    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a', guitar: t}};

    const settings = {...defaultSettings(), queryDrums: false};
    const row = buildSongDisplayRow({song: s, scoresIndex, settings});

    expect(row.score).toBe(10);
    expect(row.instrumentStatuses.find(x => x.instrumentKey === 'guitar')?.hasScore).toBe(true);
    expect(row.instrumentStatuses.find(x => x.instrumentKey === 'drums')?.isEnabled).toBe(false);
  });
});
