import {InMemoryFileStore} from '../persistence/file/fileStore.types';
import {JsonSettingsPersistence} from '../persistence/file/jsonSettingsPersistence';
import {FileJsonFestivalPersistence} from '../persistence/file/fileJsonFestivalPersistence';


describe('File-based persistence (portable)', () => {
  test('JsonSettingsPersistence load returns defaults on missing/invalid', async () => {
    const store = new InMemoryFileStore();
    const p = new JsonSettingsPersistence(store, 'settings.json');

    const s1 = await p.loadSettings();
    expect(s1.degreeOfParallelism).toBe(16);

    await store.writeText('settings.json', '{not-json');
    const s2 = await p.loadSettings();
    expect(s2.degreeOfParallelism).toBe(16);

    await store.writeText('settings.json', JSON.stringify({degreeOfParallelism: 5, queryLead: false}));
    const s3 = await p.loadSettings();
    expect(s3.degreeOfParallelism).toBe(5);
    expect(s3.queryLead).toBe(false);
  });

  test('JsonSettingsPersistence save writes pretty JSON', async () => {
    const store = new InMemoryFileStore();
    const p = new JsonSettingsPersistence(store, 'settings.json');

    await p.saveSettings({
      degreeOfParallelism: 3,
      queryLead: true,
      queryDrums: false,
      queryVocals: false,
      queryBass: false,
      queryProLead: false,
      queryProBass: false,
    });

    const txt = await store.readText('settings.json');
    expect(txt).toContain('\n');
    expect(txt).toContain('"degreeOfParallelism": 3');
  });

  test('JsonSettingsPersistence save ignores circular/unserializable input', async () => {
    const store = new InMemoryFileStore();
    const p = new JsonSettingsPersistence(store, 'settings.json');
    const circular: any = {degreeOfParallelism: 1};
    circular.self = circular;
    await p.saveSettings(circular);
    expect(await store.exists('settings.json')).toBe(false);
  });

  test('FileJsonFestivalPersistence loads/saves scores and songs when paths set', async () => {
    const store = new InMemoryFileStore();
    const p = new FileJsonFestivalPersistence(store, {scoresPath: 'scores.json', songsPath: 'songs.json'});

    expect(await p.loadScores()).toEqual([]);
    expect(await p.loadSongs()).toEqual([]);

    await p.saveScores([{songId: 'a', title: 'A', artist: 'X'} as any]);
    await p.saveSongs([{track: {su: 'a', tt: 'A', an: 'X', in: {}}} as any]);

    const scores = await p.loadScores();
    const songs = await p.loadSongs();
    expect(scores[0].songId).toBe('a');
    expect(songs[0].track.su).toBe('a');
  });

  test('FileJsonFestivalPersistence loadScores/loadSongs accept valid array JSON', async () => {
    const store = new InMemoryFileStore();
    await store.writeText('scores.json', JSON.stringify([{songId: 'a'}]));
    await store.writeText('songs.json', JSON.stringify([{track: {su: 'a', in: {}}}]));
    const p = new FileJsonFestivalPersistence(store, {scoresPath: 'scores.json', songsPath: 'songs.json'});
    const scores = await p.loadScores();
    const songs = await p.loadSongs();
    expect(scores.length).toBe(1);
    expect(songs.length).toBe(1);
  });

  test('FileJsonFestivalPersistence handles invalid JSON shapes and missing songsPath', async () => {
    const store = new InMemoryFileStore();
    await store.writeText('scores.json', JSON.stringify({not: 'an array'}));

    const p = new FileJsonFestivalPersistence(store, {scoresPath: 'scores.json'});
    expect(await p.loadScores()).toEqual([]);
    expect(await p.loadSongs()).toEqual([]);

    // no-op paths should not throw
    await p.saveSongs([{track: {su: 'x', in: {}}} as any]);
  });

  test('FileJsonFestivalPersistence loadSongs returns [] when JSON is non-array', async () => {
    const store = new InMemoryFileStore();
    await store.writeText('songs.json', JSON.stringify({not: 'an array'}));
    const p = new FileJsonFestivalPersistence(store, {scoresPath: 'scores.json', songsPath: 'songs.json'});
    expect(await p.loadSongs()).toEqual([]);
  });

  test("FileJsonFestivalPersistence treats JSON 'null' as empty list", async () => {
    const store = new InMemoryFileStore();
    await store.writeText('scores.json', 'null');
    await store.writeText('songs.json', 'null');

    const p = new FileJsonFestivalPersistence(store, {scoresPath: 'scores.json', songsPath: 'songs.json'});
    expect(await p.loadScores()).toEqual([]);
    expect(await p.loadSongs()).toEqual([]);
  });

  test('FileJsonFestivalPersistence swallows read errors and serialization failures', async () => {
    // Force readText to throw (exists=true but read fails) => catch => []
    const throwingStore = {
      async exists() {
        return true;
      },
      async readText() {
        throw new Error('read failed');
      },
      async writeText() {
        throw new Error('write failed');
      },
    };
    const p1 = new FileJsonFestivalPersistence(throwingStore as any, {scoresPath: 'scores.json', songsPath: 'songs.json'});
    expect(await p1.loadScores()).toEqual([]);
    expect(await p1.loadSongs()).toEqual([]);

    // Force JSON serialization failure (circular) => savePretty returns '' => should no-op (no file created)
    const store = new InMemoryFileStore();
    const p2 = new FileJsonFestivalPersistence(store, {scoresPath: 'scores.json', songsPath: 'songs.json'});
    const circular: any[] = [];
    circular.push(circular);
    await p2.saveScores(circular as any);
    await p2.saveSongs(circular as any);
    expect(await store.exists('scores.json')).toBe(false);
    expect(await store.exists('songs.json')).toBe(false);
  });
});
