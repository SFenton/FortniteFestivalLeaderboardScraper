import {ScoreTracker} from '../models';
import {buildV1LeaderboardUrl, parseV1LeaderboardPage, updateTrackerFromV1} from '../epic/leaderboardV1';

describe('leaderboardV1', () => {
  test('buildV1LeaderboardUrl matches expected structure', () => {
    const url = buildV1LeaderboardUrl({songId: 'abc', api: 'Solo_Vocals', accountId: 'acc', page: 0});
    expect(url).toContain('/api/v1/leaderboards/FNFestival/alltime_abc_Solo_Vocals');
    expect(url).toContain('/alltime/acc');
    expect(url).toContain('teamAccountIds=acc');
  });

  test('buildV1LeaderboardUrl defaults page to 0 when omitted', () => {
    const url = buildV1LeaderboardUrl({songId: 'abc', api: 'Solo_Vocals', accountId: 'acc'});
    expect(url).toContain('page=0');
  });

  test('parseV1LeaderboardPage returns null on invalid', () => {
    expect(parseV1LeaderboardPage(null)).toBeNull();
    expect(parseV1LeaderboardPage('x')).toBeNull();
    expect(parseV1LeaderboardPage('{bad')).toBeNull();
  });

  test('parseV1LeaderboardPage supports team_id and teamId', () => {
    const body = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [
        {
          team_id: 'acc',
          rank: 12,
          percentile: 0.01,
          sessionHistory: [{trackedStats: {SCORE: 1000, ACCURACY: 990000, FULL_COMBO: 1, STARS_EARNED: 6, SEASON: 2}}],
        },
        {
          teamId: 'other',
          rank: 1,
          sessionHistory: [{trackedStats: {SCORE: 9999}}],
        },
      ],
    });
    const page = parseV1LeaderboardPage(body);
    expect(page?.entries.length).toBe(2);
    expect(page?.entries[0].team_id).toBe('acc');
    expect(page?.entries[1].team_id).toBe('other');
    expect(page?.entries[0].score).toBe(1000);
  });

  test('parseV1LeaderboardPage ignores non-object entries and non-object sessionHistory', () => {
    const body = JSON.stringify({
      page: 'nope',
      totalPages: null,
      entries: [
        123,
        {team_id: 'acc', rank: 'x', pointsEarned: 1, percentile: 'y', sessionHistory: 'nope'},
        {team_id: 'acc2', rank: 2, sessionHistory: [{trackedStats: 'nope'}]},
      ],
    });
    const page = parseV1LeaderboardPage(body)!;
    expect(page.page).toBe(0);
    expect(page.totalPages).toBe(0);
    expect(page.entries.length).toBe(2);
    expect(page.entries[0].rank).toBeUndefined();
  });

  test('parseV1LeaderboardPage treats non-array entries as empty list', () => {
    const body = JSON.stringify({page: 1, totalPages: 2, entries: null});
    const page = parseV1LeaderboardPage(body)!;
    expect(page.entries).toEqual([]);
  });

  test('parseV1LeaderboardPage handles non-object sessionHistory elements', () => {
    const body = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [{team_id: 'acc', sessionHistory: [123, {trackedStats: {SCORE: 5}}]}],
    });
    const page = parseV1LeaderboardPage(body)!;
    expect(page.entries[0].sessionHistory?.length).toBe(2);
    expect(page.entries[0].score).toBe(5);
  });

  test('parseV1LeaderboardPage ignores NaN/Infinity numeric fields', () => {
    const body = JSON.stringify({
      page: Number.NaN,
      totalPages: Number.POSITIVE_INFINITY,
      entries: [
        {
          team_id: 'acc',
          rank: Number.NaN,
          pointsEarned: Number.POSITIVE_INFINITY,
          percentile: Number.NaN,
          sessionHistory: [{trackedStats: {SCORE: Number.NaN, ACCURACY: Number.POSITIVE_INFINITY}}],
        },
      ],
    });
    const page = parseV1LeaderboardPage(body)!;
    expect(page.page).toBe(0);
    expect(page.totalPages).toBe(0);
    expect(page.entries[0].rank).toBeUndefined();
    expect(page.entries[0].pointsEarned).toBeUndefined();
    expect(page.entries[0].percentile).toBeUndefined();
    // bestScore should remain 0 when SCORE is not a finite number
    expect(page.entries[0].score).toBe(0);
  });

  test('updateTrackerFromV1 updates rank/score/stats and derived strings', () => {
    const body = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [
        {
          team_id: 'acc',
          rank: 50,
          percentile: 0.02,
          sessionHistory: [
            {trackedStats: {SCORE: 100, ACCURACY: 900000, FULL_COMBO: 0, STARS_EARNED: 4, SEASON: 1, DIFFICULTY: 2}},
            {trackedStats: {SCORE: 200, ACCURACY: 950000, FULL_COMBO: 1, STARS_EARNED: 5, SEASON: 3, DIFFICULTY: 3}},
          ],
        },
      ],
    });
    const page = parseV1LeaderboardPage(body)!;
    const t = updateTrackerFromV1({page, accountId: 'acc', difficulty: 7, existing: new ScoreTracker()});
    expect(t.rank).toBe(50);
    expect(t.maxScore).toBe(200);
    expect(t.isFullCombo).toBe(true);
    expect(t.numStars).toBe(5);
    expect(t.seasonAchieved).toBe(3);
    expect(t.rawPercentile).toBe(0.02);
    expect(t.calculatedNumEntries).toBeGreaterThanOrEqual(50);
    expect(t.percentHitFormatted).toContain('%');
    expect(t.leaderboardPercentileFormatted).toContain('Top');
    expect(t.gameDifficulty).toBe(3);
  });

  test('updateTrackerFromV1 can create a new tracker when existing is omitted', () => {
    const body = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [{team_id: 'acc', rank: 1, percentile: 0.5, sessionHistory: [{trackedStats: {SCORE: 10}}]}],
    });
    const page = parseV1LeaderboardPage(body)!;
    const t = updateTrackerFromV1({page, accountId: 'acc', difficulty: 1});
    expect(t).toBeInstanceOf(ScoreTracker);
    expect(t.maxScore).toBe(10);
    expect(t.gameDifficulty).toBe(-1); // no DIFFICULTY in response => stays unknown
  });

  test('updateTrackerFromV1 does not overwrite a higher existing score', () => {
    const existing = new ScoreTracker();
    existing.maxScore = 999;

    const body = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [{team_id: 'acc', rank: 2, percentile: 0.5, sessionHistory: [{trackedStats: {SCORE: 10}}]}],
    });
    const page = parseV1LeaderboardPage(body)!;
    const t = updateTrackerFromV1({page, accountId: 'acc', difficulty: 1, existing});
    expect(t.maxScore).toBe(999);
    expect(t.rank).toBe(2);
  });

  test('updateTrackerFromV1 keeps tracker uninitialized when rank/score are missing', () => {
    const body = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [{team_id: 'acc', sessionHistory: []}],
    });
    const page = parseV1LeaderboardPage(body)!;
    const t = updateTrackerFromV1({page, accountId: 'acc', difficulty: 1, existing: new ScoreTracker()});
    expect(t.initialized).toBe(false);
    expect(t.maxScore).toBe(0);
    expect(t.rank).toBe(0);
  });

  test('updateTrackerFromV1 enforces estimate >= rank when percentile > 1', () => {
    const body = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [{team_id: 'acc', rank: 100, percentile: 2, sessionHistory: [{trackedStats: {SCORE: 1}}]}],
    });
    const page = parseV1LeaderboardPage(body)!;
    const t = updateTrackerFromV1({page, accountId: 'acc', difficulty: 1, existing: new ScoreTracker()});
    expect(t.calculatedNumEntries).toBe(100);
  });

  test('updateTrackerFromV1 handles missing stats/percentile and caps estimate', () => {
    const body = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [
        {
          team_id: 'acc',
          rank: 100,
          percentile: 1e-9, // too small => no estimate update
          sessionHistory: [{trackedStats: {SCORE: 1000}}],
        },
      ],
    });
    const page = parseV1LeaderboardPage(body)!;
    const t = updateTrackerFromV1({page, accountId: 'acc', difficulty: 1, existing: new ScoreTracker()});
    expect(t.rank).toBe(100);
    expect(t.maxScore).toBe(1000);

    // Now force estimate capping (must be > 1e-9 to enter estimate block)
    t.rawPercentile = 0.000000002;
    const body2 = JSON.stringify({
      page: 0,
      totalPages: 1,
      entries: [{team_id: 'acc', rank: 9999999, percentile: 0.000000002, sessionHistory: []}],
    });
    const page2 = parseV1LeaderboardPage(body2)!;
    const t2 = updateTrackerFromV1({page: page2, accountId: 'acc', difficulty: 1, existing: t});
    expect(t2.calculatedNumEntries).toBeLessThanOrEqual(10_000_000);
  });

  test('updateTrackerFromV1 uses empty-string fallback when entry has no team id', () => {
    const body = JSON.stringify({page: 0, totalPages: 1, entries: [{rank: 1, sessionHistory: []}]});
    const page = parseV1LeaderboardPage(body)!;
    const t = updateTrackerFromV1({page, accountId: 'acc', difficulty: 1, existing: new ScoreTracker()});
    expect(t.rank).toBe(0);
    expect(t.maxScore).toBe(0);
  });

  test('updateTrackerFromV1 no-ops if player not present', () => {
    const body = JSON.stringify({page: 0, totalPages: 1, entries: []});
    const page = parseV1LeaderboardPage(body)!;
    const existing = new ScoreTracker();
    existing.maxScore = 123;
    const t = updateTrackerFromV1({page, accountId: 'acc', difficulty: 1, existing});
    expect(t.maxScore).toBe(123);
  });
});
