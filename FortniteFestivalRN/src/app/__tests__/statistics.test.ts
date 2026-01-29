import {ScoreTracker} from '../../core/models';
import type {LeaderboardData} from '../../core/models';
import {buildInstrumentStats, buildTopSongCategories} from '../statistics/statistics';

describe('app/statistics', () => {
  test('buildInstrumentStats aggregates basic metrics', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true,
      maxScore: 100,
      numStars: 6,
      isFullCombo: true,
      percentHit: 1000000,
      rank: 10,
      rawPercentile: 0.02,
      totalEntries: 100,
    });

    const boards: LeaderboardData[] = [{songId: 'a', title: 'A', artist: 'X', guitar: t}];

    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar');
    expect(lead?.songsPlayed).toBe(1);
    expect(lead?.fcCount).toBe(1);
    expect(lead?.top5PercentCount).toBeGreaterThanOrEqual(1);
  });

  test('buildTopSongCategories returns weighted and unweighted categories', () => {
    const mk = (id: string, pct: number, entries: number): LeaderboardData => {
      const t = Object.assign(new ScoreTracker(), {initialized: true, rawPercentile: pct, totalEntries: entries, percentHit: 1000000});
      return {songId: id, title: id.toUpperCase(), artist: 'X', guitar: t};
    };

    const boards: LeaderboardData[] = [mk('a', 0.02, 1000), mk('b', 0.03, 10), mk('c', 0.01, 50)];
    const cats = buildTopSongCategories({boards});
    expect(cats.some(c => c.key.startsWith('stats_top_five_'))).toBe(true);
    expect(cats.some(c => c.key.startsWith('stats_top_five_weighted_'))).toBe(true);
  });
});
