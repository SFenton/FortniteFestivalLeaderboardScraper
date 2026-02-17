import {ScoreTracker} from '@festival/core';
import type {LeaderboardData} from '@festival/core';
import {buildScoreRows} from '../scoreRows';

describe('app/scores/buildScoreRows', () => {
  test('filters, selects instrument tracker, and sorts', () => {
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 200, percentHit: 990000, numStars: 5, isFullCombo: false});
    t1.refreshDerived();
    const t2 = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 980000, numStars: 6, isFullCombo: true});
    t2.refreshDerived();

    const scores: LeaderboardData[] = [
      {songId: 'a', title: 'Alpha', artist: 'Zed', guitar: t1},
      {songId: 'b', title: 'Beta', artist: 'Ann', guitar: t2},
    ];

    const rows = buildScoreRows({scores, instrument: 'Lead', filterText: 'alpha', sortColumn: 'Score', sortDesc: true});
    expect(rows.map(r => r.songId)).toEqual(['a']);
    expect(rows[0].percent).toBe('99.00%');
    expect(rows[0].starText.length).toBeGreaterThan(0);
  });
});
