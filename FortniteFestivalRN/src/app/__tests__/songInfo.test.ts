import {ScoreTracker} from '@festival/core';
import type {LeaderboardData, Song} from '@festival/core';
import {buildSongInfoInstrumentRows, composeRankOutOf, formatPercent, formatSeason} from '../songInfo/songInfo';

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
