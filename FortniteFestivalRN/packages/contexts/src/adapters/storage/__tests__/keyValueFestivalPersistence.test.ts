import {KeyValueFestivalPersistence} from '../keyValueFestivalPersistence';
import {InMemoryKeyValueStore} from '../inMemoryKeyValueStore';
import type {LeaderboardData, Song} from '@festival/core';

describe('KeyValueFestivalPersistence', () => {
  test('loadScores returns [] on missing key', async () => {
    const store = new InMemoryKeyValueStore();
    const p = new KeyValueFestivalPersistence(store, {songsKey: 's', scoresKey: 'c'});
    await expect(p.loadScores()).resolves.toEqual([]);
  });

  test('saveScores then loadScores round-trips', async () => {
    const store = new InMemoryKeyValueStore();
    const p = new KeyValueFestivalPersistence(store, {songsKey: 's', scoresKey: 'c'});

    const scores: LeaderboardData[] = [{songId: 'id', title: 't', artist: 'a'}];
    await p.saveScores(scores);
    const loaded = await p.loadScores();

    expect(loaded).toHaveLength(1);
    expect(loaded[0]?.songId).toBe('id');
  });

  test('saveSongs then loadSongs round-trips', async () => {
    const store = new InMemoryKeyValueStore();
    const p = new KeyValueFestivalPersistence(store, {songsKey: 's', scoresKey: 'c'});

    const songs: Song[] = [{track: {su: 'song', tt: 'Title'}}];
    await p.saveSongs(songs);
    const loaded = await p.loadSongs();

    expect(loaded).toHaveLength(1);
    expect(loaded[0]?.track.su).toBe('song');
  });

  test('invalid JSON is swallowed', async () => {
    const store = new InMemoryKeyValueStore();
    await store.setItem('c', '{notjson');

    const p = new KeyValueFestivalPersistence(store, {songsKey: 's', scoresKey: 'c'});
    await expect(p.loadScores()).resolves.toEqual([]);
  });

  test('non-array JSON becomes []', async () => {
    const store = new InMemoryKeyValueStore();
    await store.setItem('c', JSON.stringify({hello: 'world'}));

    const p = new KeyValueFestivalPersistence(store, {songsKey: 's', scoresKey: 'c'});
    await expect(p.loadScores()).resolves.toEqual([]);
  });
});
