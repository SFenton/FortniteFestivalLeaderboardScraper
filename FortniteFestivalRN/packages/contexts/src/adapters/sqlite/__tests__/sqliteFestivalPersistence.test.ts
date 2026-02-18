import {SqliteFestivalPersistence} from '../sqliteFestivalPersistence';
import type {SqliteDatabase, SqliteResultSet, SqliteRowList} from '../sqliteDb.types';
import type {LeaderboardData, ScoreHistoryEntry, Song} from '@festival/core';
import {ScoreTracker} from '@festival/core';

function rowsOf<T>(items: T[]): SqliteRowList<T> {
  return {
    length: items.length,
    item: (index: number) => items[index]!,
  };
}

function resultOf<T>(items: T[]): SqliteResultSet<T> {
  return {rows: rowsOf(items)};
}

describe('SqliteFestivalPersistence (adapter)', () => {
  test('ensureSchema runs on first call', async () => {
    const executeSql = jest.fn(async (sql: string) => {
      if (sql.startsWith('SELECT')) return resultOf([]);
      return resultOf([]);
    });
    const db: SqliteDatabase = {executeSql};

    const p = new SqliteFestivalPersistence(db);
    await p.loadSongs();

    expect(executeSql).toHaveBeenCalled();
    const calls = executeSql.mock.calls.map(c => c[0]);
    expect(calls.some((s: string) => s.includes('CREATE TABLE IF NOT EXISTS Songs'))).toBe(true);
    expect(calls.some((s: string) => s.includes('CREATE TABLE IF NOT EXISTS Scores'))).toBe(true);
    expect(calls.some((s: string) => s.includes('CREATE TABLE IF NOT EXISTS ScoreHistory'))).toBe(true);
    expect(calls.some((s: string) => s.includes('IX_ScoreHist_Song'))).toBe(true);
  });

  test('loadSongs maps db rows to Song', async () => {
    const executeSql = jest.fn(async (sql: string) => {
      if (sql.startsWith('SELECT')) {
        return resultOf([
          {
            SongId: 'abc',
            Title: 'T',
            Artist: 'A',
            ActiveDate: '2025-01-01T00:00:00.000Z',
            LastModified: '2025-01-02T00:00:00.000Z',
            ImagePath: 'img.png',
            LeadDiff: 3,
            BassDiff: 2,
            VocalsDiff: 1,
            DrumsDiff: 4,
            ReleaseYear: 1999,
            Tempo: 120,
            PlasticGuitarDiff: 5,
            PlasticBassDiff: 6,
            PlasticDrumsDiff: 7,
            ProVocalsDiff: 0,
          },
        ]);
      }
      return resultOf([]);
    });

    const p = new SqliteFestivalPersistence({executeSql});
    const songs = await p.loadSongs();

    expect(songs).toHaveLength(1);
    expect(songs[0]?.track.su).toBe('abc');
    expect(songs[0]?.track.tt).toBe('T');
    expect(songs[0]?.track.in?.gr).toBe(3);
    expect(songs[0]?.track.in?.pg).toBe(5);
  });

  test('saveSongs upserts each song row', async () => {
    const executeSql = jest.fn(async () => resultOf([]));
    const db: SqliteDatabase = {executeSql};
    const p = new SqliteFestivalPersistence(db);

    const songs: Song[] = [
      {track: {su: 's1', tt: 'One', an: 'A', in: {gr: 1}}},
      {track: {su: 's2', tt: 'Two', an: 'B', in: {ba: 2}}},
    ];

    await p.saveSongs(songs);

    const calls = executeSql.mock.calls.map(c => c[0]);
    const upserts = calls.filter((s: string) => s.includes('INSERT INTO Songs'));
    expect(upserts.length).toBeGreaterThanOrEqual(2);
  });

  test('loadScores maps trackers and derived fields', async () => {
    const executeSql = jest.fn(async (sql: string) => {
      if (sql.startsWith('SELECT sc.SongId')) {
        return resultOf([
          {
            SongId: 'song',
            Title: 'T',
            Artist: 'A',
            GuitarScore: 100,
            GuitarDiff: 3,
            GuitarStars: 5,
            GuitarFC: 1,
            GuitarPct: 987600,
            GuitarSeason: 12,
            GuitarRank: 44,
            GuitarTotal: 500,
            GuitarRawPct: 0.0144,
            GuitarCalcTotal: 600,

            // No drums row -> null/undefined tracker
            DrumsScore: null,
          },
        ]);
      }
      return resultOf([]);
    });

    const p = new SqliteFestivalPersistence({executeSql});
    const scores = await p.loadScores();

    expect(scores).toHaveLength(1);
    expect(scores[0]?.songId).toBe('song');
    expect(scores[0]?.guitar?.initialized).toBe(true);
    expect(scores[0]?.guitar?.percentHitFormatted).toBe('98.76%');
    expect(scores[0]?.drums).toBeUndefined();
  });

  test('saveScores skips entries with no initialized scores', async () => {
    const executeSql = jest.fn(async () => resultOf([]));
    const p = new SqliteFestivalPersistence({executeSql});

    const empty: LeaderboardData = {songId: 's', title: 't', artist: 'a'};
    await p.saveScores([empty]);

    const calls = executeSql.mock.calls.map(c => c[0]);
    expect(calls.some((s: string) => s.includes('INSERT INTO Scores'))).toBe(false);
  });

  test('saveScores persists one initialized tracker', async () => {
    const executeSql = jest.fn(async () => resultOf([]));
    const p = new SqliteFestivalPersistence({executeSql});

    const t = new ScoreTracker();
    t.initialized = true;
    t.maxScore = 123;
    t.difficulty = 4;
    t.numStars = 6;
    t.isFullCombo = true;
    t.percentHit = 900000;
    t.seasonAchieved = 1;
    t.rank = 2;
    t.totalEntries = 333;
    t.rawPercentile = 0.2;
    t.calculatedNumEntries = 444;

    const ld: LeaderboardData = {
      songId: 'id',
      title: 'Title',
      artist: 'Artist',
      guitar: t,
    };

    await p.saveScores([ld]);

    const sqls = executeSql.mock.calls.map(c => c[0]);
    expect(sqls.some((s: string) => s.includes('INSERT INTO Songs'))).toBe(true);
    expect(sqls.some((s: string) => s.includes('INSERT INTO Scores'))).toBe(true);
  });

  test('loadScoreHistory maps db rows to ScoreHistoryEntry', async () => {
    const executeSql = jest.fn(async (sql: string) => {
      if (typeof sql === 'string' && sql.includes('FROM ScoreHistory')) {
        return resultOf([
          {
            Id: 1,
            SongId: 'song1',
            Instrument: 'Solo_Guitar',
            OldScore: 100,
            NewScore: 200,
            OldRank: 50,
            NewRank: 30,
            Accuracy: 987600,
            IsFullCombo: 1,
            Stars: 5,
            Percentile: 0.05,
            Season: 12,
            ScoreAchievedAt: '2025-06-01T00:00:00Z',
            SeasonRank: 15,
            AllTimeRank: 42,
            ChangedAt: '2025-06-01T12:00:00Z',
          },
        ]);
      }
      return resultOf([]);
    });

    const p = new SqliteFestivalPersistence({executeSql});
    const entries = await p.loadScoreHistory();

    expect(entries).toHaveLength(1);
    expect(entries[0].songId).toBe('song1');
    expect(entries[0].instrument).toBe('Solo_Guitar');
    expect(entries[0].oldScore).toBe(100);
    expect(entries[0].newScore).toBe(200);
    expect(entries[0].isFullCombo).toBe(true);
    expect(entries[0].seasonRank).toBe(15);
    expect(entries[0].allTimeRank).toBe(42);
    expect(entries[0].changedAt).toBe('2025-06-01T12:00:00Z');
  });

  test('loadScoreHistory filters by songId and instrument', async () => {
    const executeSql = jest.fn(async (sql: string) => {
      if (typeof sql === 'string' && sql.includes('FROM ScoreHistory')) {
        // Verify the query includes WHERE clauses
        expect(sql).toContain('WHERE');
        expect(sql).toContain('SongId = ?');
        expect(sql).toContain('Instrument = ?');
        return resultOf([]);
      }
      return resultOf([]);
    });

    const p = new SqliteFestivalPersistence({executeSql});
    await p.loadScoreHistory('song1', 'Solo_Guitar');

    // Verify params were passed
    const historyCall = executeSql.mock.calls.find(
      (c: any[]) => typeof c[0] === 'string' && c[0].includes('FROM ScoreHistory'),
    );
    expect(historyCall).toBeDefined();
    expect(historyCall![1]).toEqual(['song1', 'Solo_Guitar']);
  });

  test('saveScoreHistory inserts entries', async () => {
    const executeSql = jest.fn(async () => resultOf([]));
    const p = new SqliteFestivalPersistence({executeSql});

    const entries: ScoreHistoryEntry[] = [
      {
        songId: 's1',
        instrument: 'Solo_Guitar',
        oldScore: 100,
        newScore: 200,
        isFullCombo: true,
        stars: 5,
        changedAt: '2025-06-01T00:00:00Z',
      },
    ];

    await p.saveScoreHistory(entries);

    const sqls = executeSql.mock.calls.map((c: any[]) => c[0]);
    expect(sqls.some((s: string) => s.includes('INSERT INTO ScoreHistory'))).toBe(true);

    const insertCall = executeSql.mock.calls.find(
      (c: any[]) => typeof c[0] === 'string' && c[0].includes('INSERT INTO ScoreHistory'),
    );
    expect(insertCall).toBeDefined();
    const params = insertCall![1] as unknown[];
    expect(params[0]).toBe('s1');           // songId
    expect(params[1]).toBe('Solo_Guitar');  // instrument
    expect(params[7]).toBe(1);             // isFullCombo -> 1
  });
});
