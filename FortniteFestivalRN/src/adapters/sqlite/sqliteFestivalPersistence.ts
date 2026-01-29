import type {FestivalPersistence} from '../../core/persistence';
import {ScoreTracker} from '../../core/models';
import type {LeaderboardData, Song} from '../../core/models';
import type {InstrumentKey} from '../../core/instruments';
import type {SqliteDatabase, SqliteTransaction} from './sqliteDb.types';

type DbSongRow = {
  SongId: string;
  Title?: string | null;
  Artist?: string | null;
  ActiveDate?: string | null;
  LastModified?: string | null;
  ImagePath?: string | null;
  LeadDiff?: number | null;
  BassDiff?: number | null;
  VocalsDiff?: number | null;
  DrumsDiff?: number | null;
  ProLeadDiff?: number | null;
  ProBassDiff?: number | null;
  ReleaseYear?: number | null;
  Tempo?: number | null;
  PlasticGuitarDiff?: number | null;
  PlasticBassDiff?: number | null;
  PlasticDrumsDiff?: number | null;
  ProVocalsDiff?: number | null;
};

type DbScoreRow = {
  SongId: string;
  Title?: string | null;
  Artist?: string | null;

  GuitarScore?: number | null;
  GuitarDiff?: number | null;
  GuitarStars?: number | null;
  GuitarFC?: number | null;
  GuitarPct?: number | null;
  GuitarSeason?: number | null;
  GuitarRank?: number | null;

  DrumsScore?: number | null;
  DrumsDiff?: number | null;
  DrumsStars?: number | null;
  DrumsFC?: number | null;
  DrumsPct?: number | null;
  DrumsSeason?: number | null;
  DrumsRank?: number | null;

  BassScore?: number | null;
  BassDiff?: number | null;
  BassStars?: number | null;
  BassFC?: number | null;
  BassPct?: number | null;
  BassSeason?: number | null;
  BassRank?: number | null;

  VocalsScore?: number | null;
  VocalsDiff?: number | null;
  VocalsStars?: number | null;
  VocalsFC?: number | null;
  VocalsPct?: number | null;
  VocalsSeason?: number | null;
  VocalsRank?: number | null;

  ProGuitarScore?: number | null;
  ProGuitarDiff?: number | null;
  ProGuitarStars?: number | null;
  ProGuitarFC?: number | null;
  ProGuitarPct?: number | null;
  ProGuitarSeason?: number | null;
  ProGuitarRank?: number | null;

  ProBassScore?: number | null;
  ProBassDiff?: number | null;
  ProBassStars?: number | null;
  ProBassFC?: number | null;
  ProBassPct?: number | null;
  ProBassSeason?: number | null;
  ProBassRank?: number | null;

  GuitarTotal?: number | null;
  DrumsTotal?: number | null;
  BassTotal?: number | null;
  VocalsTotal?: number | null;
  ProGuitarTotal?: number | null;
  ProBassTotal?: number | null;

  GuitarRawPct?: number | null;
  DrumsRawPct?: number | null;
  BassRawPct?: number | null;
  VocalsRawPct?: number | null;
  ProGuitarRawPct?: number | null;
  ProBassRawPct?: number | null;

  GuitarCalcTotal?: number | null;
  DrumsCalcTotal?: number | null;
  BassCalcTotal?: number | null;
  VocalsCalcTotal?: number | null;
  ProGuitarCalcTotal?: number | null;
  ProBassCalcTotal?: number | null;
};

function safeInt(v: unknown): number {
  return typeof v === 'number' && Number.isFinite(v) ? Math.trunc(v) : 0;
}

function safeStr(v: unknown): string {
  return typeof v === 'string' ? v : '';
}

function readTracker(row: DbScoreRow, prefix: string): ScoreTracker | undefined {
  const score = (row as any)[`${prefix}Score`];
  if (score == null) return undefined;

  const tracker = new ScoreTracker();
  tracker.maxScore = safeInt(score);
  tracker.difficulty = safeInt((row as any)[`${prefix}Diff`]);
  tracker.numStars = safeInt((row as any)[`${prefix}Stars`]);
  tracker.isFullCombo = safeInt((row as any)[`${prefix}FC`]) === 1;
  tracker.percentHit = safeInt((row as any)[`${prefix}Pct`]);
  tracker.seasonAchieved = safeInt((row as any)[`${prefix}Season`]);
  tracker.rank = safeInt((row as any)[`${prefix}Rank`]);
  tracker.initialized = tracker.maxScore > 0;

  tracker.totalEntries = safeInt((row as any)[`${prefix}Total`]);
  tracker.rawPercentile =
    typeof (row as any)[`${prefix}RawPct`] === 'number' ? ((row as any)[`${prefix}RawPct`] as number) : 0;
  tracker.calculatedNumEntries = safeInt((row as any)[`${prefix}CalcTotal`]);
  tracker.refreshDerived();
  return tracker;
}

function hasAnyScore(ld: LeaderboardData): boolean {
  return (
    ld.guitar?.initialized === true ||
    ld.drums?.initialized === true ||
    ld.bass?.initialized === true ||
    ld.vocals?.initialized === true ||
    ld.pro_guitar?.initialized === true ||
    ld.pro_bass?.initialized === true
  );
}

function scoreParamsForTracker(t: ScoreTracker | undefined): number[] {
  if (!t) return [0, 0, 0, 0, 0, 0, 0];
  return [
    safeInt(t.maxScore),
    safeInt(t.difficulty),
    safeInt(t.numStars),
    t.isFullCombo ? 1 : 0,
    safeInt(t.percentHit),
    safeInt(t.seasonAchieved),
    safeInt(t.rank),
  ];
}

export class SqliteFestivalPersistence implements FestivalPersistence {
  private readonly db: SqliteDatabase;
  private schemaReady: Promise<void> | null = null;

  constructor(db: SqliteDatabase) {
    this.db = db;
  }

  private ensureSchema(): Promise<void> {
    if (this.schemaReady) return this.schemaReady;

    this.schemaReady = (async () => {
      await this.db.executeSql('PRAGMA foreign_keys=ON');

      await this.db.executeSql(
        `CREATE TABLE IF NOT EXISTS Songs (
          SongId TEXT PRIMARY KEY,
          Title TEXT,
          Artist TEXT,
          ActiveDate TEXT,
          LastModified TEXT,
          ImagePath TEXT,
          LeadDiff INTEGER,
          BassDiff INTEGER,
          VocalsDiff INTEGER,
          DrumsDiff INTEGER,
          ProLeadDiff INTEGER,
          ProBassDiff INTEGER,
          ReleaseYear INTEGER,
          Tempo INTEGER,
          PlasticGuitarDiff INTEGER,
          PlasticBassDiff INTEGER,
          PlasticDrumsDiff INTEGER,
          ProVocalsDiff INTEGER
        );`,
      );

      await this.db.executeSql(
        `CREATE TABLE IF NOT EXISTS Scores (
          SongId TEXT PRIMARY KEY,
          GuitarScore INTEGER, GuitarDiff INTEGER, GuitarStars INTEGER, GuitarFC INTEGER, GuitarPct INTEGER, GuitarSeason INTEGER, GuitarRank INTEGER,
          DrumsScore INTEGER, DrumsDiff INTEGER, DrumsStars INTEGER, DrumsFC INTEGER, DrumsPct INTEGER, DrumsSeason INTEGER, DrumsRank INTEGER,
          BassScore INTEGER, BassDiff INTEGER, BassStars INTEGER, BassFC INTEGER, BassPct INTEGER, BassSeason INTEGER, BassRank INTEGER,
          VocalsScore INTEGER, VocalsDiff INTEGER, VocalsStars INTEGER, VocalsFC INTEGER, VocalsPct INTEGER, VocalsSeason INTEGER, VocalsRank INTEGER,
          ProGuitarScore INTEGER, ProGuitarDiff INTEGER, ProGuitarStars INTEGER, ProGuitarFC INTEGER, ProGuitarPct INTEGER, ProGuitarSeason INTEGER, ProGuitarRank INTEGER,
          ProBassScore INTEGER, ProBassDiff INTEGER, ProBassStars INTEGER, ProBassFC INTEGER, ProBassPct INTEGER, ProBassSeason INTEGER, ProBassRank INTEGER,
          GuitarTotal INTEGER, DrumsTotal INTEGER, BassTotal INTEGER, VocalsTotal INTEGER, ProGuitarTotal INTEGER, ProBassTotal INTEGER,
          GuitarRawPct REAL, DrumsRawPct REAL, BassRawPct REAL, VocalsRawPct REAL, ProGuitarRawPct REAL, ProBassRawPct REAL,
          GuitarCalcTotal INTEGER, DrumsCalcTotal INTEGER, BassCalcTotal INTEGER, VocalsCalcTotal INTEGER, ProGuitarCalcTotal INTEGER, ProBassCalcTotal INTEGER,
          FOREIGN KEY (SongId) REFERENCES Songs(SongId)
        );`,
      );

      // Best-effort migrations (ignore errors if columns already exist)
      const migrations: Array<{table: string; name: string; type: string}> = [
        {table: 'Songs', name: 'ImagePath', type: 'TEXT'},
        {table: 'Songs', name: 'ReleaseYear', type: 'INTEGER'},
        {table: 'Songs', name: 'Tempo', type: 'INTEGER'},
        {table: 'Songs', name: 'PlasticGuitarDiff', type: 'INTEGER'},
        {table: 'Songs', name: 'PlasticBassDiff', type: 'INTEGER'},
        {table: 'Songs', name: 'PlasticDrumsDiff', type: 'INTEGER'},
        {table: 'Songs', name: 'ProVocalsDiff', type: 'INTEGER'},

        {table: 'Scores', name: 'GuitarRank', type: 'INTEGER'},
        {table: 'Scores', name: 'DrumsRank', type: 'INTEGER'},
        {table: 'Scores', name: 'BassRank', type: 'INTEGER'},
        {table: 'Scores', name: 'VocalsRank', type: 'INTEGER'},
        {table: 'Scores', name: 'ProGuitarRank', type: 'INTEGER'},
        {table: 'Scores', name: 'ProBassRank', type: 'INTEGER'},

        {table: 'Scores', name: 'GuitarTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'DrumsTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'BassTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'VocalsTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'ProGuitarTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'ProBassTotal', type: 'INTEGER'},

        {table: 'Scores', name: 'GuitarRawPct', type: 'REAL'},
        {table: 'Scores', name: 'DrumsRawPct', type: 'REAL'},
        {table: 'Scores', name: 'BassRawPct', type: 'REAL'},
        {table: 'Scores', name: 'VocalsRawPct', type: 'REAL'},
        {table: 'Scores', name: 'ProGuitarRawPct', type: 'REAL'},
        {table: 'Scores', name: 'ProBassRawPct', type: 'REAL'},

        {table: 'Scores', name: 'GuitarCalcTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'DrumsCalcTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'BassCalcTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'VocalsCalcTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'ProGuitarCalcTotal', type: 'INTEGER'},
        {table: 'Scores', name: 'ProBassCalcTotal', type: 'INTEGER'},
      ];

      for (const m of migrations) {
        try {
          await this.db.executeSql(`ALTER TABLE ${m.table} ADD COLUMN ${m.name} ${m.type}`);
        } catch {
          // ignore
        }
      }
    })();

    return this.schemaReady;
  }

  async loadScores(): Promise<LeaderboardData[]> {
    await this.ensureSchema();

    const res = await this.db.executeSql<DbScoreRow>(
      `SELECT s.SongId, s.Title, s.Artist,
        sc.GuitarScore, sc.GuitarDiff, sc.GuitarStars, sc.GuitarFC, sc.GuitarPct, sc.GuitarSeason, sc.GuitarRank,
        sc.DrumsScore, sc.DrumsDiff, sc.DrumsStars, sc.DrumsFC, sc.DrumsPct, sc.DrumsSeason, sc.DrumsRank,
        sc.BassScore, sc.BassDiff, sc.BassStars, sc.BassFC, sc.BassPct, sc.BassSeason, sc.BassRank,
        sc.VocalsScore, sc.VocalsDiff, sc.VocalsStars, sc.VocalsFC, sc.VocalsPct, sc.VocalsSeason, sc.VocalsRank,
        sc.ProGuitarScore, sc.ProGuitarDiff, sc.ProGuitarStars, sc.ProGuitarFC, sc.ProGuitarPct, sc.ProGuitarSeason, sc.ProGuitarRank,
        sc.ProBassScore, sc.ProBassDiff, sc.ProBassStars, sc.ProBassFC, sc.ProBassPct, sc.ProBassSeason, sc.ProBassRank,
        sc.GuitarTotal, sc.DrumsTotal, sc.BassTotal, sc.VocalsTotal, sc.ProGuitarTotal, sc.ProBassTotal,
        sc.GuitarRawPct, sc.DrumsRawPct, sc.BassRawPct, sc.VocalsRawPct, sc.ProGuitarRawPct, sc.ProBassRawPct,
        sc.GuitarCalcTotal, sc.DrumsCalcTotal, sc.BassCalcTotal, sc.VocalsCalcTotal, sc.ProGuitarCalcTotal, sc.ProBassCalcTotal
      FROM Songs s LEFT JOIN Scores sc ON s.SongId = sc.SongId`,
    );

    const list: LeaderboardData[] = [];
    for (let i = 0; i < res.rows.length; i++) {
      const row = res.rows.item(i);
      const ld: LeaderboardData = {
        songId: safeStr(row.SongId),
        title: row.Title ?? undefined,
        artist: row.Artist ?? undefined,
      };

      ld.guitar = readTracker(row, 'Guitar');
      ld.drums = readTracker(row, 'Drums');
      ld.bass = readTracker(row, 'Bass');
      ld.vocals = readTracker(row, 'Vocals');
      ld.pro_guitar = readTracker(row, 'ProGuitar');
      ld.pro_bass = readTracker(row, 'ProBass');

      ld.dirty = false;
      list.push(ld);
    }

    return list;
  }

  async saveScores(scores: LeaderboardData[]): Promise<void> {
    await this.ensureSchema();

    const run = async (tx: SqliteTransaction): Promise<void> => {
      const songSql =
        `INSERT INTO Songs (SongId, Title, Artist, ActiveDate, LastModified, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, ReleaseYear, Tempo, PlasticGuitarDiff, PlasticBassDiff, PlasticDrumsDiff, ProVocalsDiff)
         VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
         ON CONFLICT(SongId) DO UPDATE SET Title=excluded.Title, Artist=excluded.Artist`;

      const scoreSql =
        `INSERT INTO Scores (
          SongId,
          GuitarScore,GuitarDiff,GuitarStars,GuitarFC,GuitarPct,GuitarSeason,GuitarRank,
          DrumsScore,DrumsDiff,DrumsStars,DrumsFC,DrumsPct,DrumsSeason,DrumsRank,
          BassScore,BassDiff,BassStars,BassFC,BassPct,BassSeason,BassRank,
          VocalsScore,VocalsDiff,VocalsStars,VocalsFC,VocalsPct,VocalsSeason,VocalsRank,
          ProGuitarScore,ProGuitarDiff,ProGuitarStars,ProGuitarFC,ProGuitarPct,ProGuitarSeason,ProGuitarRank,
          ProBassScore,ProBassDiff,ProBassStars,ProBassFC,ProBassPct,ProBassSeason,ProBassRank,
          GuitarTotal,DrumsTotal,BassTotal,VocalsTotal,ProGuitarTotal,ProBassTotal,
          GuitarRawPct,DrumsRawPct,BassRawPct,VocalsRawPct,ProGuitarRawPct,ProBassRawPct,
          GuitarCalcTotal,DrumsCalcTotal,BassCalcTotal,VocalsCalcTotal,ProGuitarCalcTotal,ProBassCalcTotal
        ) VALUES (
          ?,
          ?,?,?,?,?,?, ?,
          ?,?,?,?,?,?, ?,
          ?,?,?,?,?,?, ?,
          ?,?,?,?,?,?, ?,
          ?,?,?,?,?,?, ?,
          ?,?,?,?,?,?, ?,
          ?,?,?,?,?,?,
          ?,?,?,?,?,?,
          ?,?,?,?,?,?
        )
        ON CONFLICT(SongId) DO UPDATE SET
          GuitarScore=excluded.GuitarScore,GuitarDiff=excluded.GuitarDiff,GuitarStars=excluded.GuitarStars,GuitarFC=excluded.GuitarFC,GuitarPct=excluded.GuitarPct,GuitarSeason=excluded.GuitarSeason,GuitarRank=excluded.GuitarRank,GuitarTotal=excluded.GuitarTotal,GuitarRawPct=excluded.GuitarRawPct,GuitarCalcTotal=excluded.GuitarCalcTotal,
          DrumsScore=excluded.DrumsScore,DrumsDiff=excluded.DrumsDiff,DrumsStars=excluded.DrumsStars,DrumsFC=excluded.DrumsFC,DrumsPct=excluded.DrumsPct,DrumsSeason=excluded.DrumsSeason,DrumsRank=excluded.DrumsRank,DrumsTotal=excluded.DrumsTotal,DrumsRawPct=excluded.DrumsRawPct,DrumsCalcTotal=excluded.DrumsCalcTotal,
          BassScore=excluded.BassScore,BassDiff=excluded.BassDiff,BassStars=excluded.BassStars,BassFC=excluded.BassFC,BassPct=excluded.BassPct,BassSeason=excluded.BassSeason,BassRank=excluded.BassRank,BassTotal=excluded.BassTotal,BassRawPct=excluded.BassRawPct,BassCalcTotal=excluded.BassCalcTotal,
          VocalsScore=excluded.VocalsScore,VocalsDiff=excluded.VocalsDiff,VocalsStars=excluded.VocalsStars,VocalsFC=excluded.VocalsFC,VocalsPct=excluded.VocalsPct,VocalsSeason=excluded.VocalsSeason,VocalsRank=excluded.VocalsRank,VocalsTotal=excluded.VocalsTotal,VocalsRawPct=excluded.VocalsRawPct,VocalsCalcTotal=excluded.VocalsCalcTotal,
          ProGuitarScore=excluded.ProGuitarScore,ProGuitarDiff=excluded.ProGuitarDiff,ProGuitarStars=excluded.ProGuitarStars,ProGuitarFC=excluded.ProGuitarFC,ProGuitarPct=excluded.ProGuitarPct,ProGuitarSeason=excluded.ProGuitarSeason,ProGuitarRank=excluded.ProGuitarRank,ProGuitarTotal=excluded.ProGuitarTotal,ProGuitarRawPct=excluded.ProGuitarRawPct,ProGuitarCalcTotal=excluded.ProGuitarCalcTotal,
          ProBassScore=excluded.ProBassScore,ProBassDiff=excluded.ProBassDiff,ProBassStars=excluded.ProBassStars,ProBassFC=excluded.ProBassFC,ProBassPct=excluded.ProBassPct,ProBassSeason=excluded.ProBassSeason,ProBassRank=excluded.ProBassRank,ProBassTotal=excluded.ProBassTotal,ProBassRawPct=excluded.ProBassRawPct,ProBassCalcTotal=excluded.ProBassCalcTotal`;

      for (const ld of scores) {
        if (!hasAnyScore(ld)) continue;

        await tx.executeSql(songSql, [
          ld.songId,
          ld.title ?? '',
          ld.artist ?? '',
          '',
          '',
          0,
          0,
          0,
          0,
          0,
          0,
          0,
          0,
          0,
          0,
          0,
          0,
        ]);

        const g = scoreParamsForTracker(ld.guitar);
        const d = scoreParamsForTracker(ld.drums);
        const b = scoreParamsForTracker(ld.bass);
        const v = scoreParamsForTracker(ld.vocals);
        const pg = scoreParamsForTracker(ld.pro_guitar);
        const pb = scoreParamsForTracker(ld.pro_bass);

        const totals = [
          safeInt(ld.guitar?.totalEntries),
          safeInt(ld.drums?.totalEntries),
          safeInt(ld.bass?.totalEntries),
          safeInt(ld.vocals?.totalEntries),
          safeInt(ld.pro_guitar?.totalEntries),
          safeInt(ld.pro_bass?.totalEntries),
        ];

        const raws = [
          ld.guitar?.rawPercentile ?? 0,
          ld.drums?.rawPercentile ?? 0,
          ld.bass?.rawPercentile ?? 0,
          ld.vocals?.rawPercentile ?? 0,
          ld.pro_guitar?.rawPercentile ?? 0,
          ld.pro_bass?.rawPercentile ?? 0,
        ];

        const calcTotals = [
          safeInt(ld.guitar?.calculatedNumEntries),
          safeInt(ld.drums?.calculatedNumEntries),
          safeInt(ld.bass?.calculatedNumEntries),
          safeInt(ld.vocals?.calculatedNumEntries),
          safeInt(ld.pro_guitar?.calculatedNumEntries),
          safeInt(ld.pro_bass?.calculatedNumEntries),
        ];

        await tx.executeSql(scoreSql, [
          ld.songId,
          ...g,
          ...d,
          ...b,
          ...v,
          ...pg,
          ...pb,
          ...totals,
          ...raws,
          ...calcTotals,
        ]);
      }
    };

    if (this.db.transaction) {
      await this.db.transaction(async tx => {
        await run(tx);
      });
      return;
    }

    await run(this.db);
  }

  async loadSongs(): Promise<Song[]> {
    await this.ensureSchema();

    const res = await this.db.executeSql<DbSongRow>(
      'SELECT SongId, Title, Artist, ActiveDate, LastModified, ImagePath, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, ReleaseYear, Tempo, PlasticGuitarDiff, PlasticBassDiff, PlasticDrumsDiff, ProVocalsDiff FROM Songs',
    );

    const list: Song[] = [];
    for (let i = 0; i < res.rows.length; i++) {
      const r = res.rows.item(i);
      const songId = safeStr(r.SongId);
      if (!songId) continue;

      const song: Song = {
        track: {
          su: songId,
          tt: r.Title ?? undefined,
          an: r.Artist ?? undefined,
          ry: safeInt(r.ReleaseYear),
          mt: safeInt(r.Tempo),
          in: {
            gr: safeInt(r.LeadDiff),
            ba: safeInt(r.BassDiff),
            vl: safeInt(r.VocalsDiff),
            ds: safeInt(r.DrumsDiff),
            pg: safeInt(r.PlasticGuitarDiff),
            pb: safeInt(r.PlasticBassDiff),
            pd: safeInt(r.PlasticDrumsDiff),
            bd: safeInt(r.ProVocalsDiff),
          },
        },
        _activeDate: r.ActiveDate ?? undefined,
        lastModified: r.LastModified ?? undefined,
        imagePath: r.ImagePath ?? undefined,
      };

      list.push(song);
    }

    return list;
  }

  async saveSongs(songs: Song[]): Promise<void> {
    await this.ensureSchema();

    const run = async (tx: SqliteTransaction): Promise<void> => {
      const sql =
        `INSERT INTO Songs (SongId, Title, Artist, ActiveDate, LastModified, ImagePath, LeadDiff, BassDiff, VocalsDiff, DrumsDiff, ProLeadDiff, ProBassDiff, ReleaseYear, Tempo, PlasticGuitarDiff, PlasticBassDiff, PlasticDrumsDiff, ProVocalsDiff)
         VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
         ON CONFLICT(SongId) DO UPDATE SET
           Title=excluded.Title, Artist=excluded.Artist, ActiveDate=excluded.ActiveDate, LastModified=excluded.LastModified, ImagePath=excluded.ImagePath,
           LeadDiff=excluded.LeadDiff, BassDiff=excluded.BassDiff, VocalsDiff=excluded.VocalsDiff, DrumsDiff=excluded.DrumsDiff,
           ProLeadDiff=excluded.ProLeadDiff, ProBassDiff=excluded.ProBassDiff,
           ReleaseYear=excluded.ReleaseYear, Tempo=excluded.Tempo,
           PlasticGuitarDiff=excluded.PlasticGuitarDiff, PlasticBassDiff=excluded.PlasticBassDiff, PlasticDrumsDiff=excluded.PlasticDrumsDiff, ProVocalsDiff=excluded.ProVocalsDiff`;

      for (const s of songs) {
        const su = s.track?.su ?? '';
        if (!su) continue;

        await tx.executeSql(sql, [
          su,
          s.track?.tt ?? '',
          s.track?.an ?? '',
          s._activeDate ?? '',
          s.lastModified ?? '',
          s.imagePath ?? '',
          safeInt(s.track?.in?.gr),
          safeInt(s.track?.in?.ba),
          safeInt(s.track?.in?.vl),
          safeInt(s.track?.in?.ds),
          0,
          0,
          safeInt(s.track?.ry),
          safeInt(s.track?.mt),
          safeInt(s.track?.in?.pg),
          safeInt(s.track?.in?.pb),
          safeInt(s.track?.in?.pd),
          safeInt(s.track?.in?.bd),
        ]);
      }
    };

    if (this.db.transaction) {
      await this.db.transaction(async tx => {
        await run(tx);
      });
      return;
    }

    await run(this.db);
  }

  // Convenience helper mirroring the “prioritized song” filtering keys.
  // Not used by the persistence interface, but handy for callers.
  static getSongKey(songId: string): string {
    return songId;
  }

  static getInstrumentKey(inst: InstrumentKey): InstrumentKey {
    return inst;
  }
}
