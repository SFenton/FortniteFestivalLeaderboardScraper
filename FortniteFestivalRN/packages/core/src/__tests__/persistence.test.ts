import type {LeaderboardData, Song} from '../models';
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
});
