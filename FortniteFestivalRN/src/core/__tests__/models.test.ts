import {ScoreTracker} from '../models';

describe('ScoreTracker.refreshDerived', () => {
  test('formats percentHit with 2 decimals', () => {
    const t = new ScoreTracker();
    t.percentHit = 987600;
    t.refreshDerived();
    expect(t.percentHitFormatted).toBe('98.76%');
  });

  test('starsFormatted is N/A when no stars', () => {
    const t = new ScoreTracker();
    t.numStars = 0;
    t.refreshDerived();
    expect(t.starsFormatted).toBe('N/A');
  });

  test('starsFormatted caps at 6 chars', () => {
    const t = new ScoreTracker();
    t.numStars = 10;
    t.refreshDerived();
    expect(t.starsFormatted).toBe('******');
  });

  test('leaderboard percentile buckets to Top 1% minimum', () => {
    const t = new ScoreTracker();
    t.rawPercentile = 0.0001;
    t.refreshDerived();
    expect(t.leaderboardPercentileFormatted).toBe('Top 1%');
  });

  test('leaderboard percentile buckets to Top 2% etc', () => {
    const t = new ScoreTracker();
    t.rawPercentile = 0.0144; // 1.44%
    t.refreshDerived();
    expect(t.leaderboardPercentileFormatted).toBe('Top 2%');
  });

  test('leaderboard percentile caps to Top 100% when very large', () => {
    const t = new ScoreTracker();
    t.rawPercentile = 9; // 900%
    t.refreshDerived();
    expect(t.leaderboardPercentileFormatted).toBe('Top 100%');
  });

  test('clears percentile when rawPercentile not positive', () => {
    const t = new ScoreTracker();
    t.rawPercentile = 0;
    t.refreshDerived();
    expect(t.leaderboardPercentileFormatted).toBe('');
  });
});
