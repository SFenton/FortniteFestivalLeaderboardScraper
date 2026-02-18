import {KeyValueFestivalPersistence} from '../keyValueFestivalPersistence';
import {InMemoryKeyValueStore} from '../inMemoryKeyValueStore';
import type {LeaderboardData, ScoreHistoryEntry, Song} from '@festival/core';

const TEST_KEYS = {songsKey: 's', scoresKey: 'c', scoreHistoryKey: 'h'};

describe('KeyValueFestivalPersistence', () => {
  test('loadScores returns [] on missing key', async () => {
    const store = new InMemoryKeyValueStore();
    const p = new KeyValueFestivalPersistence(store, TEST_KEYS);
    await expect(p.loadScores()).resolves.toEqual([]);
  });

  test('saveScores then loadScores round-trips', async () => {
    const store = new InMemoryKeyValueStore();
    const p = new KeyValueFestivalPersistence(store, TEST_KEYS);

    const scores: LeaderboardData[] = [{songId: 'id', title: 't', artist: 'a'}];
    await p.saveScores(scores);
    const loaded = await p.loadScores();

    expect(loaded).toHaveLength(1);
    expect(loaded[0]?.songId).toBe('id');
  });

  test('saveSongs then loadSongs round-trips', async () => {
    const store = new InMemoryKeyValueStore();
    const p = new KeyValueFestivalPersistence(store, TEST_KEYS);

    const songs: Song[] = [{track: {su: 'song', tt: 'Title'}}];
    await p.saveSongs(songs);
    const loaded = await p.loadSongs();

    expect(loaded).toHaveLength(1);
    expect(loaded[0]?.track.su).toBe('song');
  });

  test('invalid JSON is swallowed', async () => {
    const store = new InMemoryKeyValueStore();
    await store.setItem('c', '{notjson');

    const p = new KeyValueFestivalPersistence(store, TEST_KEYS);
    await expect(p.loadScores()).resolves.toEqual([]);
  });

  test('non-array JSON becomes []', async () => {
    const store = new InMemoryKeyValueStore();
    await store.setItem('c', JSON.stringify({hello: 'world'}));

    const p = new KeyValueFestivalPersistence(store, TEST_KEYS);
    await expect(p.loadScores()).resolves.toEqual([]);
  });

  test('saveScoreHistory then loadScoreHistory round-trips', async () => {
    const store = new InMemoryKeyValueStore();
    const p = new KeyValueFestivalPersistence(store, TEST_KEYS);

    const entries: ScoreHistoryEntry[] = [
      {songId: 's1', instrument: 'Solo_Guitar', newScore: 200, changedAt: '2025-06-01T00:00:00Z'},
      {songId: 's2', instrument: 'Solo_Drums', newScore: 300, changedAt: '2025-06-02T00:00:00Z'},
    ];
    await p.saveScoreHistory(entries);

    const all = await p.loadScoreHistory();
    expect(all).toHaveLength(2);

    const bySong = await p.loadScoreHistory('s1');
    expect(bySong).toHaveLength(1);
    expect(bySong[0].newScore).toBe(200);

    const byInst = await p.loadScoreHistory(undefined, 'Solo_Drums');
    expect(byInst).toHaveLength(1);
    expect(byInst[0].songId).toBe('s2');
  });

  test('loadScoreHistory returns [] on missing key', async () => {
    const store = new InMemoryKeyValueStore();
    const p = new KeyValueFestivalPersistence(store, TEST_KEYS);
    await expect(p.loadScoreHistory()).resolves.toEqual([]);
  });
});
