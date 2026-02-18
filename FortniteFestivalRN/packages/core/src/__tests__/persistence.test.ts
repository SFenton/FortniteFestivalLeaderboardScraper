import type {LeaderboardData, ScoreHistoryEntry, Song} from '../models';
import {InMemoryFestivalPersistence} from '../persistence';

describe('InMemoryFestivalPersistence', () => {
  test('round-trips songs and scores', async () => {
    const p = new InMemoryFestivalPersistence();
    const songs: Song[] = [{track: {su: 'a', tt: 'A', an: 'Artist', in: {}}}];
    const scores: LeaderboardData[] = [{songId: 'a', title: 'A'}];

    await p.saveSongs(songs);
    await p.saveScores(scores);

    const loadedSongs = await p.loadSongs();
    const loadedScores = await p.loadScores();
    expect(loadedSongs).toEqual(songs);
    expect(loadedScores).toEqual(scores);

    // ensure deep copy: mutate loaded and verify persistence unchanged
    loadedSongs[0].track.tt = 'CHANGED';
    const loadedAgain = await p.loadSongs();
    expect(loadedAgain[0].track.tt).toBe('A');
  });

  test('round-trips score history', async () => {
    const p = new InMemoryFestivalPersistence();
    const entries: ScoreHistoryEntry[] = [
      {
        songId: 'song1',
        instrument: 'Solo_Guitar',
        oldScore: 100,
        newScore: 200,
        changedAt: '2025-06-01T00:00:00Z',
      },
      {
        songId: 'song1',
        instrument: 'Solo_Drums',
        newScore: 500,
        changedAt: '2025-06-02T00:00:00Z',
      },
      {
        songId: 'song2',
        instrument: 'Solo_Guitar',
        newScore: 300,
        changedAt: '2025-06-03T00:00:00Z',
      },
    ];

    await p.saveScoreHistory(entries);

    const all = await p.loadScoreHistory();
    expect(all).toHaveLength(3);

    const bySong = await p.loadScoreHistory('song1');
    expect(bySong).toHaveLength(2);

    const byInstrument = await p.loadScoreHistory(undefined, 'Solo_Guitar');
    expect(byInstrument).toHaveLength(2);

    const byBoth = await p.loadScoreHistory('song1', 'Solo_Guitar');
    expect(byBoth).toHaveLength(1);
    expect(byBoth[0].newScore).toBe(200);
  });

  test('score history is deep-copied', async () => {
    const p = new InMemoryFestivalPersistence();
    const entries: ScoreHistoryEntry[] = [
      {songId: 's', instrument: 'Solo_Guitar', newScore: 100, changedAt: '2025-01-01T00:00:00Z'},
    ];

    await p.saveScoreHistory(entries);
    const loaded = await p.loadScoreHistory();
    loaded[0].newScore = 999;

    const loadedAgain = await p.loadScoreHistory();
    expect(loadedAgain[0].newScore).toBe(100);
  });
});
