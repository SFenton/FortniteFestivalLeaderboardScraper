import {ScoreTracker} from '@festival/core';
import type {LeaderboardData, Song} from '@festival/core';
import {buildSongInfoInstrumentRows, composeRankOutOf, formatPercent, formatSeason} from '../songInfo';

describe('app/songInfo', () => {
  test('formatPercent rounds sensibly', () => {
    expect(formatPercent(1000000)).toBe('100%');
    expect(formatPercent(995000)).toBe('99.5%');
    expect(formatPercent(990000)).toBe('99%');
  });

  test('composeRankOutOf', () => {
    expect(composeRankOutOf('123', '1000')).toBe('#123 / 1000');
    expect(composeRankOutOf('123', 'N/A')).toBe('#123');
    expect(composeRankOutOf('N/A', '1000')).toBe('N/A');
  });

  test('formatPercent caps at 100%', () => {
    expect(formatPercent(1500000)).toBe('100%');
  });

  test('formatPercent returns 0% for zero or negative', () => {
    expect(formatPercent(0)).toBe('0%');
    expect(formatPercent(-100)).toBe('0%');
  });

  test('formatPercent formats 2-decimal values', () => {
    expect(formatPercent(987600)).toBe('98.76%');
  });

  test('formatSeason returns All-Time for 0', () => {
    expect(formatSeason(0)).toBe('All-Time');
    expect(formatSeason(-1)).toBe('All-Time');
  });

  test('formatSeason returns S-prefixed for positive', () => {
    expect(formatSeason(3)).toBe('S3');
  });

  test('composeRankOutOf with empty strings', () => {
    expect(composeRankOutOf('', '500')).toBe('N/A');
    expect(composeRankOutOf('', '')).toBe('N/A');
  });

  test('buildSongInfoInstrumentRows with no leaderboard data', () => {
    const song: Song = {track: {su: 'missing', tt: 'X', an: 'Y', in: {gr: 4, ba: 2, ds: 3, vl: 1}}};
    const scoresIndex: Record<string, LeaderboardData> = {};
    const rows = buildSongInfoInstrumentRows({
      song,
      instrumentOrder: ['guitar', 'bass', 'drums', 'vocals', 'pro_guitar', 'pro_bass'],
      scoresIndex,
    });
    expect(rows).toHaveLength(6);
    for (const row of rows) {
      expect(row.hasScore).toBe(false);
      expect(row.scoreDisplay).toBe('0');
      expect(row.percentDisplay).toBe('0%');
      expect(row.seasonDisplay).toBe('N/A');
      expect(row.rankOutOfDisplay).toBe('N/A');
    }
    // fallbackDifficulty from track intensities
    expect(rows[0].rawDifficulty).toBe(4); // guitar -> gr
    expect(rows[1].rawDifficulty).toBe(2); // bass -> ba
    expect(rows[2].rawDifficulty).toBe(3); // drums -> ds
    expect(rows[3].rawDifficulty).toBe(1); // vocals -> vl
    expect(rows[4].rawDifficulty).toBe(4); // pro_guitar -> pg ?? gr
    expect(rows[5].rawDifficulty).toBe(2); // pro_bass -> pb ?? ba
  });

  test('buildSongInfoInstrumentRows with pro_guitar/pro_bass fallback to pg/pb', () => {
    const song: Song = {track: {su: 'fb', tt: 'X', an: 'Y', in: {gr: 3, ba: 2, pg: 5, pb: 4}}};
    const scoresIndex: Record<string, LeaderboardData> = {};
    const rows = buildSongInfoInstrumentRows({
      song,
      instrumentOrder: ['pro_guitar', 'pro_bass'],
      scoresIndex,
    });
    expect(rows[0].rawDifficulty).toBe(5); // pg exists
    expect(rows[1].rawDifficulty).toBe(4); // pb exists
  });

  test('buildSongInfoInstrumentRows with FC shows 100%', () => {
    const song: Song = {track: {su: 'fc', tt: 'FC Song', an: 'Artist'}};
    const t = Object.assign(new ScoreTracker(), {
      initialized: true,
      maxScore: 50000,
      percentHit: 1000000,
      isFullCombo: true,
      numStars: 6,
      seasonAchieved: 0,
      gameDifficulty: 3,
      rank: 0,
      rawPercentile: 0,
      difficulty: 0,
    });
    t.refreshDerived();
    const scoresIndex: Record<string, LeaderboardData> = {fc: {songId: 'fc', guitar: t}};

    const rows = buildSongInfoInstrumentRows({song, instrumentOrder: ['guitar'], scoresIndex});
    expect(rows[0].percentDisplay).toBe('100%');
    expect(rows[0].isFullCombo).toBe(true);
    expect(rows[0].starsCount).toBe(6);
    expect(rows[0].gameDifficultyDisplay).toBe('X'); // Expert
    expect(rows[0].seasonDisplay).toBe('All-Time');
    expect(rows[0].rankDisplay).toBe('N/A');
    expect(rows[0].percentileDisplay).toBe('N/A');
    expect(rows[0].isTop5Percentile).toBe(false);
  });

  test('buildSongInfoInstrumentRows gameDifficulty values map correctly', () => {
    const mkRow = (gd: number) => {
      const song: Song = {track: {su: `gd${gd}`, tt: 'S', an: 'A'}};
      const t = Object.assign(new ScoreTracker(), {
        initialized: true, maxScore: 100, percentHit: 500000, numStars: 3,
        gameDifficulty: gd, isFullCombo: false, seasonAchieved: 1,
        rank: 0, rawPercentile: 0, difficulty: 2,
      });
      t.refreshDerived();
      const scoresIndex: Record<string, LeaderboardData> = {[`gd${gd}`]: {songId: `gd${gd}`, guitar: t}};
      return buildSongInfoInstrumentRows({song, instrumentOrder: ['guitar'], scoresIndex})[0];
    };

    expect(mkRow(-1).gameDifficultyDisplay).toBe(undefined); // '' or undefined (-1 maps to '')
    expect(mkRow(0).gameDifficultyDisplay).toBe('E');
    expect(mkRow(1).gameDifficultyDisplay).toBe('M');
    expect(mkRow(2).gameDifficultyDisplay).toBe('H');
    expect(mkRow(3).gameDifficultyDisplay).toBe('X');
  });

  test('buildSongInfoInstrumentRows with percentile data', () => {
    const song: Song = {track: {su: 'pct', tt: 'Pct', an: 'A'}};
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, maxScore: 100, percentHit: 500000, numStars: 3,
      isFullCombo: false, seasonAchieved: 1,
      rank: 5, calculatedNumEntries: 200, rawPercentile: 0.03,
      difficulty: 0,
    });
    t.refreshDerived();
    const scoresIndex: Record<string, LeaderboardData> = {pct: {songId: 'pct', guitar: t}};
    const rows = buildSongInfoInstrumentRows({song, instrumentOrder: ['guitar'], scoresIndex});
    expect(rows[0].rankDisplay).toBe('5');
    expect(rows[0].totalEntriesDisplay).toBe('200');
    expect(rows[0].rankOutOfDisplay).toBe('#5 / 200');
    expect(rows[0].isTop5Percentile).toBe(true);
    expect(rows[0].percentileDisplay).not.toBe('N/A');
  });

  test('buildSongInfoInstrumentRows with no track intensities uses 0 fallback', () => {
    const song: Song = {track: {su: 'noint', tt: 'No Int', an: 'Y'}};
    const scoresIndex: Record<string, LeaderboardData> = {};
    const rows = buildSongInfoInstrumentRows({
      song,
      instrumentOrder: ['guitar', 'bass', 'drums', 'vocals', 'pro_guitar', 'pro_bass'],
      scoresIndex,
    });
    for (const row of rows) {
      expect(row.rawDifficulty).toBe(0);
    }
  });

  test('buildSongInfoInstrumentRows with multiple instruments including uninitialized', () => {
    const song: Song = {track: {su: 'multi', tt: 'Multi', an: 'A', in: {gr: 3}}};
    const init = Object.assign(new ScoreTracker(), {
      initialized: true, maxScore: 100, percentHit: 500000, numStars: 3,
      isFullCombo: false, seasonAchieved: 1, rank: 0, rawPercentile: 0, difficulty: 0,
      gameDifficulty: -1,
    });
    init.refreshDerived();
    const uninit = new ScoreTracker(); // not initialized
    const scoresIndex: Record<string, LeaderboardData> = {
      multi: {songId: 'multi', guitar: init, drums: uninit},
    };
    const rows = buildSongInfoInstrumentRows({song, instrumentOrder: ['guitar', 'drums'], scoresIndex});
    expect(rows[0].hasScore).toBe(true);
    expect(rows[0].gameDifficultyDisplay).toBeUndefined(); // gameDifficulty -1 → '' → undefined
    expect(rows[1].hasScore).toBe(false);
    expect(rows[1].scoreDisplay).toBe('0');
  });

  test('buildSongInfoInstrumentRows builds displays from tracker', () => {
    const song: Song = {track: {su: 'a', tt: 'A', an: 'X', in: {gr: 3}}};
    const t = Object.assign(new ScoreTracker(), {
      initialized: true,
      maxScore: 12345,
      percentHit: 999000,
      isFullCombo: false,
      seasonAchieved: 2,
      rank: 1234,
      calculatedNumEntries: 1000,
      rawPercentile: 0.02,
      difficulty: 4,
    });
    t.refreshDerived();

    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a', guitar: t}};

    const rows = buildSongInfoInstrumentRows({song, instrumentOrder: ['guitar'], scoresIndex});
    expect(rows[0].name).toBe('Lead');
    expect(rows[0].starsCount).toBe(0);
    expect(rows[0].scoreDisplay).toBe('12,345');
    expect(rows[0].seasonDisplay).toBe(formatSeason(2));
    expect(rows[0].rankDisplay).toBe('1,234');
    expect(rows[0].totalEntriesDisplay).toBe('1,000');
    expect(rows[0].rankOutOfDisplay).toBe('#1,234 / 1,000');
    expect(rows[0].isTop5Percentile).toBe(true);
  });
});
