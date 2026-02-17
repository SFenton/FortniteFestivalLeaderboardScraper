import {ScoreTracker} from '@festival/core';
import type {LeaderboardData} from '@festival/core';
import {buildInstrumentStats, buildTopSongCategories} from '../statistics';

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

  test('buildInstrumentStats with no boards returns zero stats', () => {
    const stats = buildInstrumentStats({boards: [], totalSongsInLibrary: 0});
    expect(stats).toHaveLength(6); // one per instrument
    for (const s of stats) {
      expect(s.songsPlayed).toBe(0);
      expect(s.fcCount).toBe(0);
      expect(s.averageStars).toBe(0);
      expect(s.totalScore).toBe(0);
      expect(s.bestRank).toBe(0);
      expect(s.bestRankFormatted).toBe('N/A');
      expect(s.averagePercentileFormatted).toBe('N/A');
      expect(s.weightedPercentileFormatted).toBe('N/A');
    }
  });

  test('buildInstrumentStats counts percentile buckets correctly', () => {
    const mkTracker = (pct: number, entries: number): ScoreTracker =>
      Object.assign(new ScoreTracker(), {initialized: true, rawPercentile: pct, totalEntries: entries, percentHit: 500000, maxScore: 100, numStars: 3, rank: 5});

    const boards: LeaderboardData[] = [
      {songId: 'a', guitar: mkTracker(0.005, 100)},  // top 0.5% → top1
      {songId: 'b', guitar: mkTracker(0.03, 100)},   // top 3% → top5
      {songId: 'c', guitar: mkTracker(0.08, 100)},   // top 8% → top10
      {songId: 'd', guitar: mkTracker(0.20, 100)},   // top 20% → top25
      {songId: 'e', guitar: mkTracker(0.40, 100)},   // top 40% → top50
      {songId: 'f', guitar: mkTracker(0.70, 100)},   // top 70% → below50
    ];

    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 6});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.top1PercentCount).toBe(1);
    expect(lead.top5PercentCount).toBe(1);
    expect(lead.top10PercentCount).toBe(1);
    expect(lead.top25PercentCount).toBe(1);
    expect(lead.top50PercentCount).toBe(1);
    expect(lead.below50PercentCount).toBe(1);
  });

  test('buildTopSongCategories handles songs with calculatedNumEntries fallback', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.05, totalEntries: 0, calculatedNumEntries: 200, percentHit: 500000,
    });
    const boards: LeaderboardData[] = [{songId: 'a', title: 'A', artist: 'X', guitar: t}];
    const cats = buildTopSongCategories({boards});
    expect(cats.length).toBeGreaterThanOrEqual(2);
  });

  test('buildTopSongCategories skips instruments with no percentile data', () => {
    const t = Object.assign(new ScoreTracker(), {initialized: true, rawPercentile: 0, percentHit: 500000});
    const boards: LeaderboardData[] = [{songId: 'a', title: 'A', artist: 'X', guitar: t}];
    const cats = buildTopSongCategories({boards});
    expect(cats.filter(c => c.key.includes('guitar'))).toHaveLength(0);
  });

  test('buildTopSongCategories handles zero/negative baseline', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.1, totalEntries: 0, calculatedNumEntries: 0, percentHit: 500000,
    });
    const boards: LeaderboardData[] = [{songId: 'a', title: 'A', artist: 'X', guitar: t}];
    const cats = buildTopSongCategories({boards});
    expect(cats.length).toBeGreaterThanOrEqual(2);
    const weighted = cats.find(c => c.key.includes('weighted'));
    expect(weighted?.songs[0].songId).toBe('a');
  });

  test('buildTopSongCategories weighted sort tiebreaker uses rawPercentile', () => {
    // Two songs with same weighted score but different raw percentiles
    const t1 = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.02, totalEntries: 100, percentHit: 500000,
    });
    const t2 = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.03, totalEntries: 100, percentHit: 500000,
    });
    // With same totalEntries and same baseline, weightScore = rawPercentile * (baseline / entries)
    // Since both have totalEntries=100, baseline=100, weightScore = rawPercentile
    // so they won't tie on weight. Let me make them tie:
    // weightScore = rawPercentile * (baseline / entries). To tie: need same rawPercentile * baseline/entries
    // If pct=0.02, entries=100 → 0.02 * (100/100) = 0.02
    // If pct=0.04, entries=200 → 0.04 * (100/200) = 0.02
    // Same weighted score but different rawPercentile → tiebreaker applies
    const t3 = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.04, totalEntries: 200, percentHit: 500000,
    });
    const boards: LeaderboardData[] = [
      {songId: 'a', title: 'A', artist: 'X', guitar: t1},
      {songId: 'b', title: 'B', artist: 'Y', guitar: t3},
    ];
    const cats = buildTopSongCategories({boards});
    const weighted = cats.find(c => c.key === 'stats_top_five_weighted_guitar');
    expect(weighted).toBeDefined();
    // t1: weighted=0.02, t3: weighted=0.02 → tie → rawPercentile: 0.02 < 0.04
    expect(weighted!.songs[0].songId).toBe('a');
    expect(weighted!.songs[1].songId).toBe('b');
  });

  test('buildInstrumentStats handles uninitialized trackers and zero percentHit', () => {
    const uninit = new ScoreTracker(); // not initialized
    const boards: LeaderboardData[] = [{songId: 'a', title: 'A', artist: 'X', guitar: uninit}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.songsPlayed).toBe(0);
    expect(lead.songsUnplayed).toBe(1);
    expect(lead.completionPercent).toBe(0);
  });

  test('buildInstrumentStats handles tracker with no maxScore', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, percentHit: 500000, numStars: 3, maxScore: 0,
      isFullCombo: false, rank: 0, rawPercentile: 0, totalEntries: 0,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.songsPlayed).toBe(1);
    expect(lead.totalScore).toBe(0);
    expect(lead.highestScore).toBe(0);
    expect(lead.bestRank).toBe(0);
    expect(lead.bestRankFormatted).toBe('N/A');
  });

  test('buildInstrumentStats detects perfect scores (percentHit >= 1000000)', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, percentHit: 1000000, numStars: 6, maxScore: 50000,
      isFullCombo: true, rank: 1, rawPercentile: 0.01, totalEntries: 100,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.perfectScoreCount).toBe(1);
    expect(lead.bestAccuracy).toBe(100);
  });

  test('buildInstrumentStats with calculatedNumEntries fallback for weight', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, percentHit: 500000, numStars: 3, maxScore: 100,
      isFullCombo: false, rank: 5, rawPercentile: 0.05,
      totalEntries: 0, calculatedNumEntries: 200,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.weightedPercentileFormatted).not.toBe('N/A');
  });

  test('buildInstrumentStats with no totalEntries or calculatedNumEntries uses weight=1', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, percentHit: 500000, numStars: 3, maxScore: 100,
      isFullCombo: false, rank: 5, rawPercentile: 0.05,
      totalEntries: 0, calculatedNumEntries: 0,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.weightedPercentileFormatted).not.toBe('N/A');
  });

  test('buildTopSongCategories exercises weighted with calculatedNumEntries fallback', () => {
    const t1 = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.03, totalEntries: 0, calculatedNumEntries: 50, percentHit: 500000,
    });
    const t2 = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.02, totalEntries: 100, percentHit: 600000,
    });
    const boards: LeaderboardData[] = [
      {songId: 'a', title: 'A', artist: 'X', guitar: t1},
      {songId: 'b', title: 'B', artist: 'Y', guitar: t2},
    ];
    const cats = buildTopSongCategories({boards});
    const weighted = cats.find(c => c.key === 'stats_top_five_weighted_guitar');
    expect(weighted).toBeDefined();
    expect(weighted!.songs.length).toBe(2);
  });

  test('buildTopSongCategories exercises weight=1 fallback when totalEntries=0 and calculatedNumEntries=0', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.05, totalEntries: 0, calculatedNumEntries: 0, percentHit: 500000,
    });
    const boards: LeaderboardData[] = [{songId: 'a', title: 'A', artist: 'X', guitar: t}];
    const cats = buildTopSongCategories({boards});
    const weighted = cats.find(c => c.key === 'stats_top_five_weighted_guitar');
    expect(weighted).toBeDefined();
  });

  test('buildInstrumentStats percentHit ?? 0 for tracker with undefined percentHit', () => {
    // ScoreTracker with percentHit = 0 (default) — covers percentHit ?? 0 fallback
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, numStars: 0, maxScore: 0,
      isFullCombo: false, rank: 0, rawPercentile: 0,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    // percentHit=0 → not included in `played` (percentHit > 0 check)
    expect(lead.songsPlayed).toBe(0);
  });

  test('buildTopSongCategories with title and artist undefined uses empty strings', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.03, totalEntries: 100, percentHit: 500000,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}]; // no title or artist
    const cats = buildTopSongCategories({boards});
    const weighted = cats.find(c => c.key === 'stats_top_five_weighted_guitar');
    expect(weighted!.songs[0].title).toBe('');
    expect(weighted!.songs[0].artist).toBe('');
  });

  test('buildTopSongCategories weightScore returns Infinity for zero rawPercentile', () => {
    // A tracker with rawPercentile=0 is already filtered out (t.rawPercentile > 0 check)
    // but let's verify the filtering
    const t0 = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0, totalEntries: 100, percentHit: 500000,
    });
    const tOk = Object.assign(new ScoreTracker(), {
      initialized: true, rawPercentile: 0.05, totalEntries: 100, percentHit: 500000,
    });
    const boards: LeaderboardData[] = [
      {songId: 'a', title: 'A', artist: 'X', guitar: t0},
      {songId: 'b', title: 'B', artist: 'Y', guitar: tOk},
    ];
    const cats = buildTopSongCategories({boards});
    const raw = cats.find(c => c.key === 'stats_top_five_guitar');
    // Only tOk should appear (t0 filtered out)
    expect(raw!.songs.length).toBe(1);
    expect(raw!.songs[0].songId).toBe('b');
  });

  test('buildInstrumentStats covers all six instruments', () => {
    const mk = (key: string): LeaderboardData => {
      const t = Object.assign(new ScoreTracker(), {
        initialized: true, percentHit: 800000, numStars: 4, maxScore: 500,
        isFullCombo: false, rank: 10, rawPercentile: 0.1, totalEntries: 100,
      });
      return {songId: key, [key]: t} as any;
    };
    const boards: LeaderboardData[] = [
      mk('guitar'), mk('drums'), mk('vocals'), mk('bass'), mk('pro_guitar'), mk('pro_bass'),
    ];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 6});
    for (const s of stats) {
      expect(s.songsPlayed).toBe(1);
    }
  });

  test('buildInstrumentStats with tracker where numStars=0 but percentHit>0', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, percentHit: 100000, numStars: 0, maxScore: 50,
      isFullCombo: false, rank: 0, rawPercentile: 0,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.songsPlayed).toBe(1);
    expect(lead.averageStars).toBe(0); // starsWithScore is empty (numStars > 0 check)
    expect(lead.threeOrLessStarCount).toBe(0);
  });

  test('buildInstrumentStats with negative rawPercentile excluded from percentiled', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, percentHit: 500000, numStars: 3, maxScore: 100,
      isFullCombo: false, rank: 5, rawPercentile: -0.1,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.averagePercentileFormatted).toBe('N/A');
    expect(lead.weightedPercentileFormatted).toBe('N/A');
  });

  test('buildInstrumentStats rawPercentile > 1 excluded from percentiled', () => {
    const t = Object.assign(new ScoreTracker(), {
      initialized: true, percentHit: 500000, numStars: 3, maxScore: 100,
      isFullCombo: false, rank: 5, rawPercentile: 1.5,
    });
    const boards: LeaderboardData[] = [{songId: 'a', guitar: t}];
    const stats = buildInstrumentStats({boards, totalSongsInLibrary: 1});
    const lead = stats.find(s => s.instrumentKey === 'guitar')!;
    expect(lead.averagePercentileFormatted).toBe('N/A');
  });
});
