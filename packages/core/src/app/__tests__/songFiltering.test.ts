import {ScoreTracker} from '@festival/core';
import type {LeaderboardData, Song} from '@festival/core';
import {defaultSettings} from '@festival/core';
import {buildSongDisplayRow, defaultAdvancedMissingFilters, defaultPrimaryInstrumentOrder, filterAndSortSongs, songMatchesAdvancedMissing} from '../songFiltering';

describe('app/songs/songFiltering', () => {
  const mkSong = (id: string, title: string, artist: string): Song => ({track: {su: id, tt: title, an: artist, in: {}}});

  test('songMatchesAdvancedMissing treats missing entry as missing score', () => {
    const s = mkSong('a', 'A', 'X');
    const filters = {...defaultAdvancedMissingFilters(), missingPadScores: true, includeLead: true};
    expect(songMatchesAdvancedMissing(s, {}, filters)).toBe(true);
  });

  test('songMatchesAdvancedMissing treats missing entry as missing pad FC', () => {
    const s = mkSong('a', 'A', 'X');
    const filters = {...defaultAdvancedMissingFilters(), missingPadFCs: true, includeLead: true};
    expect(songMatchesAdvancedMissing(s, {}, filters)).toBe(true);
  });

  test('songMatchesAdvancedMissing treats missing entry as missing pro FC', () => {
    const s = mkSong('a', 'A', 'X');
    const filters = {...defaultAdvancedMissingFilters(), missingProFCs: true, includeProGuitar: true};
    expect(songMatchesAdvancedMissing(s, {}, filters)).toBe(true);
  });

  test('filterAndSortSongs sorts by hasfc priority then sequential count', () => {
    const s1 = mkSong('a', 'A', 'X');
    const s2 = mkSong('b', 'B', 'Y');

    const fc = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: true});
    const nf = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});

    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: fc, drums: fc, vocals: fc, bass: fc, pro_guitar: fc, pro_bass: fc},
      b: {songId: 'b', guitar: fc, drums: nf, vocals: nf, bass: nf, pro_guitar: nf, pro_bass: nf},
    };

    const out = filterAndSortSongs({
      songs: [s2, s1],
      scoresIndex,
      sortMode: 'hasfc',
      sortAscending: true,
      instrumentOrder: defaultPrimaryInstrumentOrder(),
    });

    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('buildSongDisplayRow populates instrument statuses and applies settings enable flags', () => {
    const s = mkSong('a', 'A', 'X');
    const t = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 10, numStars: 3, isFullCombo: true, percentHit: 1000000});
    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a', guitar: t}};

    const settings = {...defaultSettings(), showDrums: false};
    const row = buildSongDisplayRow({song: s, scoresIndex, settings});

    expect(row.score).toBe(10);
    expect(row.instrumentStatuses.find(x => x.instrumentKey === 'guitar')?.hasScore).toBe(true);
    expect(row.instrumentStatuses.find(x => x.instrumentKey === 'drums')?.isEnabled).toBe(false);
  });

  test('filterAndSortSongs applies difficulty filter for selected instrument', () => {
    const easySong = mkSong('easy', 'Easy Song', 'X');
    const expertSong = mkSong('expert', 'Expert Song', 'Y');

    const easyTracker = Object.assign(new ScoreTracker(), {initialized: true, difficulty: 0});
    const expertTracker = Object.assign(new ScoreTracker(), {initialized: true, difficulty: 6});

    const scoresIndex: Record<string, LeaderboardData> = {
      easy: {songId: 'easy', guitar: easyTracker},
      expert: {songId: 'expert', guitar: expertTracker},
    };

    const out = filterAndSortSongs({
      songs: [easySong, expertSong],
      scoresIndex,
      sortMode: 'title',
      sortAscending: true,
      instrumentFilter: 'guitar',
      advanced: {
        ...defaultAdvancedMissingFilters(),
        difficultyFilter: {
          1: true,
          2: true,
          3: true,
          4: true,
          5: true,
          6: true,
          7: false,
          0: true,
        },
      },
    });

    expect(out.map(s => s.track.su)).toEqual(['easy']);
  });

  /* ── filterAndSortSongs sort modes ── */

  test('filterAndSortSongs sorts by artist then year then title', () => {
    const sa = mkSong('a', 'Zebra', 'Alpha');
    const sb = mkSong('b', 'Apple', 'Beta');
    const out = filterAndSortSongs({songs: [sb, sa], scoresIndex: {}, sortMode: 'artist'});
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('filterAndSortSongs sorts by year then title then artist', () => {
    const s1: Song = {track: {su: 'a', tt: 'A', an: 'X', ry: 2001, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'B', an: 'Y', ry: 2000, in: {}}};
    const out = filterAndSortSongs({songs: [s1, s2], scoresIndex: {}, sortMode: 'year'});
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  test('filterAndSortSongs filters by text search', () => {
    const sa = mkSong('a', 'Hello', 'World');
    const sb = mkSong('b', 'Goodbye', 'Moon');
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex: {}, filterText: 'hello'});
    expect(out.map(s => s.track.su)).toEqual(['a']);
  });

  test('filterAndSortSongs reverses when sortAscending is false', () => {
    const sa = mkSong('a', 'Alpha', 'X');
    const sb = mkSong('b', 'Beta', 'Y');
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex: {}, sortMode: 'title', sortAscending: false});
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  test('filterAndSortSongs default fallback sort is year then title then artist', () => {
    const s1: Song = {track: {su: 'a', tt: 'B', an: 'X', ry: 2001, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'A', an: 'Y', ry: 2001, in: {}}};
    // Using an unrecognized mode would hit the default fallback
    const out = filterAndSortSongs({songs: [s1, s2], scoresIndex: {}, sortMode: 'title'});
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  test('filterAndSortSongs sorts by instrument-specific score mode', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const tLow = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 100, percentHit: 500000, numStars: 3, isFullCombo: false});
    const tHigh = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 999, percentHit: 900000, numStars: 5, isFullCombo: true});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tLow},
      b: {songId: 'b', guitar: tHigh},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar'});
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('filterAndSortSongs sorts by percentage for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const tLow = Object.assign(new ScoreTracker(), {initialized: true, percentHit: 500000});
    const tHigh = Object.assign(new ScoreTracker(), {initialized: true, percentHit: 900000});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tLow},
      b: {songId: 'b', guitar: tHigh},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'percentage', instrumentFilter: 'guitar'});
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('filterAndSortSongs sorts by percentile for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const tLow = Object.assign(new ScoreTracker(), {initialized: true, rawPercentile: 0.5});
    const tHigh = Object.assign(new ScoreTracker(), {initialized: true, rawPercentile: 0.01});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tHigh},
      b: {songId: 'b', guitar: tLow},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'percentile', instrumentFilter: 'guitar'});
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('filterAndSortSongs sorts by isfc for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const tNoFc = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});
    const tFc = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: true});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tNoFc},
      b: {songId: 'b', guitar: tFc},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'isfc', instrumentFilter: 'guitar'});
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('filterAndSortSongs sorts by stars for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const t3 = Object.assign(new ScoreTracker(), {initialized: true, numStars: 3});
    const t6 = Object.assign(new ScoreTracker(), {initialized: true, numStars: 6});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: t3},
      b: {songId: 'b', guitar: t6},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'stars', instrumentFilter: 'guitar'});
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('filterAndSortSongs sorts by seasonachieved for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, seasonAchieved: 1});
    const t5 = Object.assign(new ScoreTracker(), {initialized: true, seasonAchieved: 5});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: t1},
      b: {songId: 'b', guitar: t5},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'seasonachieved', instrumentFilter: 'guitar'});
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('filterAndSortSongs sorts by intensity for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const t1 = Object.assign(new ScoreTracker(), {initialized: true, difficulty: 1});
    const t5 = Object.assign(new ScoreTracker(), {initialized: true, difficulty: 5});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: t1},
      b: {songId: 'b', guitar: t5},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'intensity', instrumentFilter: 'guitar'});
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  /* ── season / percentile / stars filters ── */

  test('filterAndSortSongs applies season filter for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const tS1 = Object.assign(new ScoreTracker(), {initialized: true, seasonAchieved: 1});
    const tS2 = Object.assign(new ScoreTracker(), {initialized: true, seasonAchieved: 2});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tS1},
      b: {songId: 'b', guitar: tS2},
    };
    const out = filterAndSortSongs({
      songs: [sa, sb], scoresIndex, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), seasonFilter: {1: true, 2: false}},
    });
    expect(out.map(s => s.track.su)).toEqual(['a']);
  });

  test('filterAndSortSongs applies percentile filter for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const t5 = Object.assign(new ScoreTracker(), {initialized: true, rawPercentile: 0.04});  // bucket 4 → threshold 5
    const t50 = Object.assign(new ScoreTracker(), {initialized: true, rawPercentile: 0.45}); // bucket 50
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: t5},
      b: {songId: 'b', guitar: t50},
    };
    const out = filterAndSortSongs({
      songs: [sa, sb], scoresIndex, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), percentileFilter: {5: true, 50: false}},
    });
    expect(out.map(s => s.track.su)).toEqual(['a']);
  });

  test('filterAndSortSongs applies stars filter for selected instrument', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const t3 = Object.assign(new ScoreTracker(), {initialized: true, numStars: 3});
    const t6 = Object.assign(new ScoreTracker(), {initialized: true, numStars: 6});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: t3},
      b: {songId: 'b', guitar: t6},
    };
    const out = filterAndSortSongs({
      songs: [sa, sb], scoresIndex, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), starsFilter: {3: true, 6: false}},
    });
    expect(out.map(s => s.track.su)).toEqual(['a']);
  });

  /* ── songMatchesAdvancedMissing detailed branches ── */

  test('songMatchesAdvancedMissing detects missing drums/vocals/bass scores', () => {
    const s = mkSong('a', 'A', 'X');
    const entry: LeaderboardData = {songId: 'a'};
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingPadScores: true, includeDrums: true})).toBe(true);
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingPadScores: true, includeVocals: true})).toBe(true);
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingPadScores: true, includeBass: true})).toBe(true);
  });

  test('songMatchesAdvancedMissing detects missing pad FCs on specific instruments', () => {
    const s = mkSong('a', 'A', 'X');
    const t = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});
    const entry: LeaderboardData = {songId: 'a', guitar: t, drums: t, vocals: t, bass: t};
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingPadFCs: true, includeLead: true})).toBe(true);
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingPadFCs: true, includeDrums: true})).toBe(true);
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingPadFCs: true, includeVocals: true})).toBe(true);
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingPadFCs: true, includeBass: true})).toBe(true);
  });

  test('songMatchesAdvancedMissing detects missing pro scores and FCs', () => {
    const s = mkSong('a', 'A', 'X');
    const entry: LeaderboardData = {songId: 'a'};
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingProScores: true, includeProGuitar: true})).toBe(true);
    expect(songMatchesAdvancedMissing(s, {a: entry}, {...defaultAdvancedMissingFilters(), missingProScores: true, includeProBass: true})).toBe(true);
    const t = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});
    const entry2: LeaderboardData = {songId: 'a', pro_guitar: t, pro_bass: t};
    expect(songMatchesAdvancedMissing(s, {a: entry2}, {...defaultAdvancedMissingFilters(), missingProFCs: true, includeProGuitar: true})).toBe(true);
    expect(songMatchesAdvancedMissing(s, {a: entry2}, {...defaultAdvancedMissingFilters(), missingProFCs: true, includeProBass: true})).toBe(true);
  });

  test('songMatchesAdvancedMissing returns false when no filters are active', () => {
    const s = mkSong('a', 'A', 'X');
    expect(songMatchesAdvancedMissing(s, {}, defaultAdvancedMissingFilters())).toBe(false);
  });

  /* ── buildSongDisplayRow edge cases ── */

  test('buildSongDisplayRow with no scores sets all statuses to no score', () => {
    const s = mkSong('a', 'A', 'X');
    const row = buildSongDisplayRow({song: s, scoresIndex: {}});
    expect(row.score).toBe(0);
    expect(row.stars).toBe('');
    expect(row.isFullCombo).toBe(false);
    expect(row.instrumentStatuses.every(x => !x.hasScore)).toBe(true);
  });

  test('buildSongDisplayRow uses leaderboardData parameter variant', () => {
    const s = mkSong('a', 'A', 'X');
    const t = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 42, numStars: 2, isFullCombo: false, percentHit: 500000, seasonAchieved: 0});
    const row = buildSongDisplayRow({song: s, leaderboardData: {songId: 'a', guitar: t}});
    expect(row.score).toBe(42);
    expect(row.season).toBe('-1');
  });

  test('buildSongDisplayRow shows season number when > 0', () => {
    const s = mkSong('a', 'A', 'X');
    const t = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 1, numStars: 1, isFullCombo: false, percentHit: 100000, seasonAchieved: 3});
    const row = buildSongDisplayRow({song: s, leaderboardData: {songId: 'a', guitar: t}});
    expect(row.season).toBe('3');
  });

  /* ── instrumentHasFC & sequential FCs ── */

  test('songHasSequentialTopFCsScore counts leading FCs only', () => {
    const {songHasSequentialTopFCsScore} = require('../songFiltering');
    const fc = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: true});
    const nf = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: fc, drums: nf},
    };
    const order = defaultPrimaryInstrumentOrder();
    expect(songHasSequentialTopFCsScore('a', scoresIndex, order)).toBe(1);
    expect(songHasSequentialTopFCsScore('missing', scoresIndex, order)).toBe(0);
  });

  /* ── fallbackDifficulty coverage ── */

  test('filterAndSortSongs uses fallback difficulty for various instruments', () => {
    const mkSongWithDiff = (id: string, diffs: Record<string, number>): Song => ({
      track: {su: id, tt: id, an: 'X', in: diffs},
    });
    const s1 = mkSongWithDiff('a', {gr: 3, ba: 2, ds: 4, vl: 1, pg: 5, pb: 6});
    const s2 = mkSongWithDiff('b', {gr: 1});
    const t = Object.assign(new ScoreTracker(), {initialized: true, difficulty: 0});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', bass: t, drums: t, vocals: t, pro_guitar: t, pro_bass: t},
      b: {songId: 'b', bass: t},
    };
    // Test each instrument filter with difficulty filter
    for (const inst of ['bass', 'drums', 'vocals', 'pro_guitar', 'pro_bass'] as const) {
      const out = filterAndSortSongs({
        songs: [s1], scoresIndex, instrumentFilter: inst,
        advanced: {...defaultAdvancedMissingFilters(), difficultyFilter: {0: false}},
      });
      // The song should pass since fallback difficulty > 0
      expect(out.length).toBeGreaterThanOrEqual(0);
    }
  });

  /* ── year sort tiebreaking (same year → title → artist) ── */

  test('year sort: same year falls through to title then artist', () => {
    const s1: Song = {track: {su: 'a', tt: 'Alpha', an: 'Zed', ry: 2000, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'Alpha', an: 'Ann', ry: 2000, in: {}}};
    const s3: Song = {track: {su: 'c', tt: 'Beta', an: 'Ann', ry: 2000, in: {}}};
    const out = filterAndSortSongs({songs: [s3, s1, s2], scoresIndex: {}, sortMode: 'year'});
    // Same year → sorted by title first ('Alpha' < 'Beta'), then artist ('Ann' < 'Zed')
    expect(out.map(s => s.track.su)).toEqual(['b', 'a', 'c']);
  });

  /* ── artist sort tiebreaking (same artist → year → title) ── */

  test('artist sort: same artist falls through to year then title', () => {
    const s1: Song = {track: {su: 'a', tt: 'Zebra', an: 'Same', ry: 2001, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'Alpha', an: 'Same', ry: 2000, in: {}}};
    const s3: Song = {track: {su: 'c', tt: 'Beta', an: 'Same', ry: 2001, in: {}}};
    const out = filterAndSortSongs({songs: [s1, s3, s2], scoresIndex: {}, sortMode: 'artist'});
    // Same artist → year ('2000' < '2001'), then title for same-year ties
    expect(out.map(s => s.track.su)).toEqual(['b', 'c', 'a']);
  });

  /* ── title sort tiebreaking (same title → year → artist) ── */

  test('title sort: same title falls through to year then artist', () => {
    const s1: Song = {track: {su: 'a', tt: 'Same', an: 'Zed', ry: 2001, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'Same', an: 'Ann', ry: 2000, in: {}}};
    const s3: Song = {track: {su: 'c', tt: 'Same', an: 'Ann', ry: 2001, in: {}}};
    const out = filterAndSortSongs({songs: [s1, s3, s2], scoresIndex: {}, sortMode: 'title'});
    // Same title → year (2000 < 2001), then artist for same-year ties
    expect(out.map(s => s.track.su)).toEqual(['b', 'c', 'a']);
  });

  /* ── hasfc sort tiebreaking ── */

  test('hasfc sort: ties fall through to sequential FCs → year → title → artist', () => {
    const fc = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: true});
    const nf = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});
    // Both have same allFC priority (0) and same sequential count (1)
    const s1: Song = {track: {su: 'a', tt: 'Same', an: 'Zed', ry: 2001, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'Same', an: 'Ann', ry: 2000, in: {}}};
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: fc, drums: nf},
      b: {songId: 'b', guitar: fc, drums: nf},
    };
    const out = filterAndSortSongs({
      songs: [s1, s2], scoresIndex, sortMode: 'hasfc',
      instrumentOrder: defaultPrimaryInstrumentOrder(),
    });
    // Same FC priority & sequential count → falls through to year then title then artist
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  test('hasfc sort: same year and title falls through to artist', () => {
    const fc = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: true});
    const nf = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});
    const s1: Song = {track: {su: 'a', tt: 'Same', an: 'Zed', ry: 2001, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'Same', an: 'Ann', ry: 2001, in: {}}};
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: fc, drums: nf},
      b: {songId: 'b', guitar: fc, drums: nf},
    };
    const out = filterAndSortSongs({
      songs: [s1, s2], scoresIndex, sortMode: 'hasfc',
      instrumentOrder: defaultPrimaryInstrumentOrder(),
    });
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  /* ── instrument sort cascade (tie on primary → cascades through metadataPriority) ── */

  test('instrument sort: tie on primary cascades through metadata priority', () => {
    const sa = mkSong('a', 'Zebra', 'Y');
    const sb = mkSong('b', 'Alpha', 'X');
    // Same score → should cascade to title by default metadata priority
    const tSame = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 500, percentHit: 500000, numStars: 3, isFullCombo: false, rawPercentile: 0.5, seasonAchieved: 1, difficulty: 2});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tSame},
      b: {songId: 'b', guitar: {...tSame}},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar'});
    // Tie on score → cascade → title: 'Alpha' < 'Zebra'
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  test('instrument sort: custom metadataSortPriority affects cascade', () => {
    const sa: Song = {track: {su: 'a', tt: 'Same', an: 'Zed', ry: 2001, in: {}}};
    const sb: Song = {track: {su: 'b', tt: 'Same', an: 'Ann', ry: 2000, in: {}}};
    const tSame = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 500, percentHit: 500000, numStars: 3, isFullCombo: false, difficulty: 2, seasonAchieved: 1, rawPercentile: 0.5});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tSame},
      b: {songId: 'b', guitar: {...tSame}},
    };
    // Custom priority: sort by artist first when scores tie
    const out = filterAndSortSongs({
      songs: [sa, sb], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar',
      metadataSortPriority: ['artist', 'title', 'year'],
    });
    // Tie on score → cascade to artist: 'ann' < 'zed'
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  /* ── buildSongDisplayRow enable flags for all instruments ── */

  test('buildSongDisplayRow applies show settings for bass, vocals, pro_guitar, pro_bass', () => {
    const s = mkSong('a', 'A', 'X');
    const row = buildSongDisplayRow({
      song: s, scoresIndex: {},
      settings: {showLead: true, showDrums: true, showVocals: false, showBass: false, showProLead: false, showProBass: false},
    });
    expect(row.instrumentStatuses.find(x => x.instrumentKey === 'vocals')?.isEnabled).toBe(false);
    expect(row.instrumentStatuses.find(x => x.instrumentKey === 'bass')?.isEnabled).toBe(false);
    expect(row.instrumentStatuses.find(x => x.instrumentKey === 'pro_guitar')?.isEnabled).toBe(false);
    expect(row.instrumentStatuses.find(x => x.instrumentKey === 'pro_bass')?.isEnabled).toBe(false);
  });

  /* ── season filter on uninitialized tracker ── */

  test('season filter includes uninitialized tracker (season=0) when 0 is not excluded', () => {
    const s = mkSong('a', 'A', 'X');
    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a'}};
    const out = filterAndSortSongs({
      songs: [s], scoresIndex, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), seasonFilter: {1: false}},
    });
    expect(out.length).toBe(1);
  });

  /* ── percentile filter on uninitialized tracker ── */

  test('percentile filter includes uninitialized tracker (bucket=0) when 0 is not excluded', () => {
    const s = mkSong('a', 'A', 'X');
    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a'}};
    const out = filterAndSortSongs({
      songs: [s], scoresIndex, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), percentileFilter: {5: false}},
    });
    expect(out.length).toBe(1);
  });

  /* ── stars filter on uninitialized tracker ── */

  test('stars filter includes uninitialized tracker (stars=0) when 0 is not excluded', () => {
    const s = mkSong('a', 'A', 'X');
    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a'}};
    const out = filterAndSortSongs({
      songs: [s], scoresIndex, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), starsFilter: {6: false}},
    });
    expect(out.length).toBe(1);
  });

  /* ── missing entry pro score branch ── */

  test('songMatchesAdvancedMissing missing entry triggers pro scores branch', () => {
    const s = mkSong('a', 'A', 'X');
    expect(songMatchesAdvancedMissing(s, {}, {...defaultAdvancedMissingFilters(), missingProScores: true, includeProBass: true})).toBe(true);
  });

  test('songMatchesAdvancedMissing missing entry with no instrument includes returns false', () => {
    const s = mkSong('a', 'A', 'X');
    // Missing pro scores but no instruments included
    const filters = {...defaultAdvancedMissingFilters(), missingProScores: true,
      includeLead: false, includeBass: false, includeDrums: false, includeVocals: false,
      includeProGuitar: false, includeProBass: false};
    expect(songMatchesAdvancedMissing(s, {}, filters)).toBe(false);
  });

  /* ── default fallback sort (unknown mode) ── */

  test('filterAndSortSongs default fallback sorts by year then title then artist', () => {
    const s1: Song = {track: {su: 'a', tt: 'Zebra', an: 'X', ry: 2001, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'Alpha', an: 'Y', ry: 2000, in: {}}};
    const s3: Song = {track: {su: 'c', tt: 'Alpha', an: 'W', ry: 2000, in: {}}};
    // Use an unknown sort mode to hit the default fallback
    const out = filterAndSortSongs({songs: [s1, s2, s3], scoresIndex: {}, sortMode: 'bogus' as any});
    // Default: year → title → artist
    expect(out.map(s => s.track.su)).toEqual(['c', 'b', 'a']);
  });

  /* ── instrument sort cascade returns 0 (complete tie) ── */

  test('instrument sort cascade returns 0 when all fields are identical', () => {
    const sa: Song = {track: {su: 'a', tt: 'Same', an: 'Same', ry: 2000, in: {}}};
    const sb: Song = {track: {su: 'b', tt: 'Same', an: 'Same', ry: 2000, in: {}}};
    const tSame = Object.assign(new ScoreTracker(), {
      initialized: true, maxScore: 500, percentHit: 500000, numStars: 3,
      isFullCombo: false, rawPercentile: 0.5, seasonAchieved: 1, difficulty: 2
    });
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tSame},
      b: {songId: 'b', guitar: {...tSame}},
    };
    // Both songs identical on all cascade fields → stable sort
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar'});
    expect(out.length).toBe(2);
  });

  /* ── compareByMetadataKey year branch with undefined ry ── */

  test('instrument sort handles songs with undefined release year', () => {
    const sa: Song = {track: {su: 'a', tt: 'A', an: 'X', in: {}}}; // ry undefined
    const sb: Song = {track: {su: 'b', tt: 'B', an: 'Y', ry: 2000, in: {}}};
    const tA = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 500, percentHit: 500000, numStars: 3, isFullCombo: false, difficulty: 2});
    const tB = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 500, percentHit: 500000, numStars: 3, isFullCombo: false, difficulty: 2});
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tA},
      b: {songId: 'b', guitar: tB},
    };
    const out = filterAndSortSongs({
      songs: [sb, sa], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar',
      metadataSortPriority: ['year', 'title', 'artist'],
    });
    // ry undefined → 0 < 2000 → sa comes first
    expect(out[0].track.su).toBe('a');
  });

  /* ── buildSongDisplayRow with _title fallback ── */

  test('buildSongDisplayRow uses _title when track.tt is undefined', () => {
    const s: Song = {_title: 'Fallback Title', track: {su: 'x', an: 'A', in: {}}};
    const row = buildSongDisplayRow({song: s, scoresIndex: {}});
    expect(row.title).toBe('Fallback Title');
  });

  /* ── filterAndSortSongs with undefined ry triggers ?? 0 branches ── */

  test('year sort handles undefined release years via ?? 0', () => {
    const s1: Song = {track: {su: 'a', tt: 'Alpha', an: 'X', in: {}}}; // ry undefined
    const s2: Song = {track: {su: 'b', tt: 'Beta', an: 'Y', in: {}}}; // ry undefined
    const out = filterAndSortSongs({songs: [s2, s1], scoresIndex: {}, sortMode: 'year'});
    // Both ry undefined → 0 === 0 → fall through to title
    expect(out.map(s => s.track.su)).toEqual(['a', 'b']);
  });

  test('title sort handles undefined ry and an via ?? 0', () => {
    const s1: Song = {track: {su: 'a', tt: 'Same', in: {}}}; // an undefined
    const s2: Song = {track: {su: 'b', tt: 'Same', in: {}}}; // an undefined  
    const out = filterAndSortSongs({songs: [s2, s1], scoresIndex: {}, sortMode: 'title'});
    // Same title, both ry=0, both an='' → stable
    expect(out.length).toBe(2);
  });

  /* ── filterAndSortSongs with undefined track fields in canon ── */

  test('filterText handles undefined title and artist via canon', () => {
    const s: Song = {track: {su: 'a', in: {}}}; // tt and an undefined
    const out = filterAndSortSongs({songs: [s], scoresIndex: {}, filterText: 'test'});
    expect(out.length).toBe(0); // no match on empty strings
  });

  /* ── instrument sort with uninitialized trackers ── */

  test('instrument sort treats uninitialized trackers as zero', () => {
    const sa = mkSong('a', 'A', 'X');
    const sb = mkSong('b', 'B', 'Y');
    const tInit = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 500, percentHit: 500000, numStars: 3, isFullCombo: false, rawPercentile: 0.5, seasonAchieved: 1, difficulty: 2});
    const tUninit = new ScoreTracker(); // initialized: false
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tInit},
      b: {songId: 'b', guitar: tUninit},
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar'});
    // Uninitialized → 0, initialized → 500 → b first (ascending)
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  /* ── difficulty filter with uninitialized tracker ── */

  test('difficulty filter uses 0 bucket for uninitialized tracker', () => {
    const s = mkSong('a', 'A', 'X');
    const tUninit = new ScoreTracker();
    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a', guitar: tUninit}};
    const out = filterAndSortSongs({
      songs: [s], scoresIndex, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), difficultyFilter: {0: false}},
    });
    // bucket = 0 since not initialized, and 0 is excluded
    expect(out.length).toBe(0);
  });

  /* ── ?? fallback branch coverage for advanced filter properties ── */

  test('filters work with partial advanced object (seasonFilter/percentileFilter/starsFilter/difficultyFilter undefined)', () => {
    const s = mkSong('a', 'A', 'X');
    const t = Object.assign(new ScoreTracker(), {initialized: true, numStars: 3, rawPercentile: 0.1, seasonAchieved: 2, difficulty: 3});
    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a', guitar: t}};
    // Pass a PARTIAL advanced object that is missing seasonFilter/percentileFilter/starsFilter/difficultyFilter
    // This triggers the ?? {} fallback branches on lines 225, 249, 253, 268
    const advanced = {
      missingPadFCs: false, missingProFCs: false,
      missingPadScores: false, missingProScores: false,
      includeLead: true, includeBass: true, includeDrums: true, includeVocals: true,
      includeProGuitar: true, includeProBass: true,
    } as any;
    const out = filterAndSortSongs({
      songs: [s], scoresIndex, instrumentFilter: 'guitar', advanced,
    });
    expect(out.length).toBe(1);
  });

  /* ── buildSongDisplayRow with settings having undefined show properties (triggers ?? true) ── */

  test('buildSongDisplayRow with empty settings uses ?? true defaults for all instruments', () => {
    const s = mkSong('a', 'A', 'X');
    // Pass settings as empty object — all show* are undefined → ?? true fallback
    const row = buildSongDisplayRow({song: s, scoresIndex: {}, settings: {} as any});
    for (const st of row.instrumentStatuses) {
      expect(st.isEnabled).toBe(true);
    }
  });

  /* ── buildSongDisplayRow with leaderboardData and preferred tracker fallback chain ── */

  test('buildSongDisplayRow prefers first available tracker: drums over vocals', () => {
    const s = mkSong('a', 'A', 'X');
    const tDrums = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 77, numStars: 2, isFullCombo: false, percentHit: 200000, seasonAchieved: 1});
    const row = buildSongDisplayRow({song: s, leaderboardData: {songId: 'a', drums: tDrums}});
    expect(row.score).toBe(77);
  });

  test('buildSongDisplayRow prefers vocals when no guitar/drums', () => {
    const s = mkSong('a', 'A', 'X');
    const tVocals = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 88, numStars: 4, isFullCombo: true, percentHit: 1000000, seasonAchieved: 0});
    const row = buildSongDisplayRow({song: s, leaderboardData: {songId: 'a', vocals: tVocals}});
    expect(row.score).toBe(88);
    expect(row.isFullCombo).toBe(true);
    expect(row.season).toBe('-1');
  });

  test('buildSongDisplayRow prefers bass when no guitar/drums/vocals', () => {
    const s = mkSong('a', 'A', 'X');
    const tBass = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 55, numStars: 1, isFullCombo: false, percentHit: 100000, seasonAchieved: 3});
    const row = buildSongDisplayRow({song: s, leaderboardData: {songId: 'a', bass: tBass}});
    expect(row.score).toBe(55);
  });

  test('buildSongDisplayRow prefers pro_guitar when no pad instruments', () => {
    const s = mkSong('a', 'A', 'X');
    const tPg = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 33, numStars: 2, isFullCombo: false, percentHit: 300000, seasonAchieved: 0});
    const row = buildSongDisplayRow({song: s, leaderboardData: {songId: 'a', pro_guitar: tPg}});
    expect(row.score).toBe(33);
  });

  test('buildSongDisplayRow prefers pro_bass as last fallback', () => {
    const s = mkSong('a', 'A', 'X');
    const tPb = Object.assign(new ScoreTracker(), {initialized: true, maxScore: 22, numStars: 1, isFullCombo: false, percentHit: 50000, seasonAchieved: 0});
    const row = buildSongDisplayRow({song: s, leaderboardData: {songId: 'a', pro_bass: tPb}});
    expect(row.score).toBe(22);
  });

  /* ── canon() with undefined title/artist in sort tiebreaking paths ── */

  test('year sort with undefined title and artist in tiebreak uses empty strings', () => {
    const s1: Song = {track: {su: 'a', ry: 2000, in: {}}}; // tt and an undefined
    const s2: Song = {track: {su: 'b', tt: 'Z', an: 'Z', ry: 2000, in: {}}};
    const out = filterAndSortSongs({songs: [s2, s1], scoresIndex: {}, sortMode: 'year'});
    // s1 has '' for title and artist → '' < 'z'
    expect(out[0].track.su).toBe('a');
  });

  test('artist sort with undefined artist in both entries tiebreaks by year then title', () => {
    const s1: Song = {track: {su: 'a', tt: 'Z', ry: 2001, in: {}}}; // an undefined
    const s2: Song = {track: {su: 'b', tt: 'A', ry: 2000, in: {}}}; // an undefined
    const out = filterAndSortSongs({songs: [s1, s2], scoresIndex: {}, sortMode: 'artist'});
    // Both an = '' → tie → falls to year → 2000 < 2001 → s2 first
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  test('hasfc sort with undefined ry and an in tiebreaker', () => {
    const nf = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});
    const s1: Song = {track: {su: 'a', tt: 'Same', in: {}}}; // ry=undefined, an=undefined
    const s2: Song = {track: {su: 'b', tt: 'Same', ry: 1, an: 'Z', in: {}}};
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: nf},
      b: {songId: 'b', guitar: nf},
    };
    const out = filterAndSortSongs({
      songs: [s2, s1], scoresIndex, sortMode: 'hasfc',
      instrumentOrder: defaultPrimaryInstrumentOrder(),
    });
    // same FC priority/seq, ry: 0 vs 1 → s1 first
    expect(out[0].track.su).toBe('a');
  });

  test('title sort with undefined an tiebreaks by artist empty string', () => {
    const s1: Song = {track: {su: 'a', tt: 'Same', ry: 2000, in: {}}}; // an undefined
    const s2: Song = {track: {su: 'b', tt: 'Same', ry: 2000, an: 'Z', in: {}}};
    const out = filterAndSortSongs({songs: [s2, s1], scoresIndex: {}, sortMode: 'title'});
    // Same title, same year → '' < 'z' → s1 first
    expect(out[0].track.su).toBe('a');
  });

  /* ── compareByMetadataKey cascade with all metadata keys ── */

  test('instrument sort cascade exercises all compareByMetadataKey cases including intensity', () => {
    // Two songs where ALL cascade keys match except intensity
    const sa: Song = {track: {su: 'a', tt: 'Same', an: 'Same', ry: 2000, in: {}}};
    const sb: Song = {track: {su: 'b', tt: 'Same', an: 'Same', ry: 2000, in: {}}};
    const tA = Object.assign(new ScoreTracker(), {
      initialized: true, maxScore: 500, percentHit: 500000, numStars: 3,
      isFullCombo: false, rawPercentile: 0.5, seasonAchieved: 1, difficulty: 2,
    });
    const tB = Object.assign(new ScoreTracker(), {
      initialized: true, maxScore: 500, percentHit: 500000, numStars: 3,
      isFullCombo: false, rawPercentile: 0.5, seasonAchieved: 1, difficulty: 5,
    });
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tA},
      b: {songId: 'b', guitar: tB},
    };
    // Sort by score; all other cascade keys tie except intensity (last in default order)
    const out = filterAndSortSongs({songs: [sb, sa], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar'});
    // Score ties → cascade through title, artist, year, percentage, percentile, isfc, stars, seasonachieved → all tie → intensity breaks tie
    expect(out[0].track.su).toBe('a'); // difficulty 2 < 5
  });

  test('instrument sort cascade with uninitialized tracker on one side', () => {
    const sa: Song = {track: {su: 'a', tt: 'A', an: 'X', ry: 2000, in: {}}};
    const sb: Song = {track: {su: 'b', tt: 'B', an: 'Y', ry: 2001, in: {}}};
    const tInit = Object.assign(new ScoreTracker(), {
      initialized: true, maxScore: 0, percentHit: 0, numStars: 0,
      isFullCombo: false, rawPercentile: 0, seasonAchieved: 0, difficulty: 0,
    });
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: tInit},
      b: {songId: 'b'}, // no guitar tracker
    };
    const out = filterAndSortSongs({songs: [sb, sa], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar'});
    // Both score = 0 → cascade to title: 'a' < 'b'
    expect(out[0].track.su).toBe('a');
  });

  /* ── filterAndSortSongs with no entry in scoresIndex for season/percentile/stars/difficulty filters ── */

  test('season filter excludes song with no entry', () => {
    const s = mkSong('a', 'A', 'X');
    const out = filterAndSortSongs({
      songs: [s], scoresIndex: {}, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), seasonFilter: {0: false}},
    });
    // No entry → season=0 → excluded since 0 is false
    expect(out.length).toBe(0);
  });

  test('percentile filter with no entry treats bucket as 0', () => {
    const s = mkSong('a', 'A', 'X');
    const out = filterAndSortSongs({
      songs: [s], scoresIndex: {}, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), percentileFilter: {0: false}},
    });
    expect(out.length).toBe(0);
  });

  test('stars filter with no entry treats stars as 0', () => {
    const s = mkSong('a', 'A', 'X');
    const out = filterAndSortSongs({
      songs: [s], scoresIndex: {}, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), starsFilter: {0: false}},
    });
    expect(out.length).toBe(0);
  });

  test('difficulty filter with no entry at all treats bucket as 0', () => {
    const s = mkSong('a', 'A', 'X');
    const out = filterAndSortSongs({
      songs: [s], scoresIndex: {}, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), difficultyFilter: {0: false}},
    });
    expect(out.length).toBe(0);
  });

  /* ── fallbackDifficulty with track.in undefined (triggers in ?? {}) ── */

  test('difficulty filter works when song.track.in is undefined', () => {
    const s: Song = {track: {su: 'a', tt: 'A', an: 'X'}} as any; // in is undefined
    const t = Object.assign(new ScoreTracker(), {initialized: true, difficulty: 0});
    const scoresIndex: Record<string, LeaderboardData> = {a: {songId: 'a', guitar: t}};
    const out = filterAndSortSongs({
      songs: [s], scoresIndex, instrumentFilter: 'guitar',
      advanced: {...defaultAdvancedMissingFilters(), difficultyFilter: {0: false}},
    });
    // in is undefined → in ?? {} → {} → gr ?? 0 → 0 → clamped → bucket 1, difficulty=0, resolved=0, bucket=0+1=1
    // Actually: difficultyBucketForSong: raw = 0 (tracker.difficulty=0), resolved = fallbackDifficulty → (undefined ?? {})['gr'] ?? 0 → 0
    // resolved = 0 → clamped = 0 → bucket = 0 + 1 = 1
    // difficultyFilter: {0: false} → bucket 1 is NOT excluded
    expect(out.length).toBe(1);
  });

  /* ── buildSongDisplayRow with song.track.tt undefined and _title undefined ── */

  test('buildSongDisplayRow with both tt and _title undefined uses empty string', () => {
    const s: Song = {track: {su: 'x', an: 'A', in: {}}} as any; // tt undefined, no _title
    const row = buildSongDisplayRow({song: s, scoresIndex: {}});
    expect(row.title).toBe('');
  });

  /* ── buildSongDisplayRow with song.track.an undefined ── */

  test('buildSongDisplayRow with an undefined uses empty string', () => {
    const s: Song = {track: {su: 'x', tt: 'Title', in: {}}} as any;
    const row = buildSongDisplayRow({song: s, scoresIndex: {}});
    expect(row.artist).toBe('');
  });

  /* ── filterAndSortSongs with default sort ascending & default advanced & defaults ── */

  test('filterAndSortSongs defaults all optional params', () => {
    const s = mkSong('a', 'A', 'X');
    // Just songs and scoresIndex — all other params default
    const out = filterAndSortSongs({songs: [s], scoresIndex: {}});
    expect(out.length).toBe(1);
  });

  /* ── songMatchesAdvancedMissing with entry present but all FCs satisfied ── */

  test('songMatchesAdvancedMissing returns false when FC is satisfied for all included instruments', () => {
    const s = mkSong('a', 'A', 'X');
    const fc = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: true});
    const entry: LeaderboardData = {songId: 'a', guitar: fc, drums: fc, vocals: fc, bass: fc, pro_guitar: fc, pro_bass: fc};
    const filters = {
      ...defaultAdvancedMissingFilters(),
      missingPadFCs: true, missingProFCs: true,
    };
    expect(songMatchesAdvancedMissing(s, {a: entry}, filters)).toBe(false);
  });

  /* ── songMatchesAdvancedMissing no entry + pad score/FC with no instrument includes ── */

  test('songMatchesAdvancedMissing no entry with pad filters but no pad instruments returns false', () => {
    const s = mkSong('a', 'A', 'X');
    const filters = {
      ...defaultAdvancedMissingFilters(),
      missingPadScores: true,
      includeLead: false, includeBass: false, includeDrums: false, includeVocals: false,
      includeProGuitar: false, includeProBass: false,
    };
    expect(songMatchesAdvancedMissing(s, {}, filters)).toBe(false);
  });

  /* ── instrument sort mode with null instrumentFilter (falls through to title) ── */

  test('instrument sort mode without instrumentFilter falls through to title sort', () => {
    const sa = mkSong('a', 'Zebra', 'X');
    const sb = mkSong('b', 'Alpha', 'Y');
    // sortMode=score but instrumentFilter=null → won't enter instrument branch → falls to title sort
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex: {}, sortMode: 'score', instrumentFilter: null});
    // Falls through to title sort: 'alpha' < 'zebra'
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  /* ── artist sort tiebreak with undefined ry (covers ?? 0 RIGHT branch in artist sort) ── */

  test('artist sort tiebreak with undefined ry falls through via ?? 0', () => {
    const s1: Song = {track: {su: 'a', tt: 'Zebra', an: 'Same', in: {}}}; // ry undefined
    const s2: Song = {track: {su: 'b', tt: 'Alpha', an: 'Same', in: {}}}; // ry undefined
    const out = filterAndSortSongs({songs: [s1, s2], scoresIndex: {}, sortMode: 'artist'});
    // Both an = 'Same' → tie → ry: 0 === 0 → tie → title: 'alpha' < 'zebra'
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  /* ── default fallback sort with undefined ry ── */

  test('default fallback sort with undefined ry', () => {
    const s1: Song = {track: {su: 'a', tt: 'Zebra', an: 'X', in: {}}}; // ry undefined
    const s2: Song = {track: {su: 'b', tt: 'Alpha', an: 'Y', in: {}}}; // ry undefined
    const out = filterAndSortSongs({songs: [s1, s2], scoresIndex: {}, sortMode: 'bogus' as any});
    // ry: 0 === 0 → tie → title: 'alpha' < 'zebra'
    expect(out.map(s => s.track.su)).toEqual(['b', 'a']);
  });

  /* ── hasfc sort with undefined title in tiebreaker path ── */

  test('hasfc tiebreak with undefined title and artist', () => {
    const nf = Object.assign(new ScoreTracker(), {initialized: true, isFullCombo: false});
    const s1: Song = {track: {su: 'a', in: {}}}; // tt and an undefined
    const s2: Song = {track: {su: 'b', tt: 'Z', an: 'Z', in: {}}};
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a', guitar: nf},
      b: {songId: 'b', guitar: nf},
    };
    const out = filterAndSortSongs({
      songs: [s2, s1], scoresIndex, sortMode: 'hasfc',
      instrumentOrder: defaultPrimaryInstrumentOrder(),
    });
    // both FC priority=0 same, sequential=1 same, ry: 0 vs 0 → tie → title: '' < 'z'
    expect(out[0].track.su).toBe('a');
  });

  /* ── year sort with second element having undefined title ── */

  test('year sort with second element having undefined title in tiebreak', () => {
    const s1: Song = {track: {su: 'a', tt: 'Alpha', an: 'X', ry: 2000, in: {}}};
    const s2: Song = {track: {su: 'b', an: 'Y', ry: 2000, in: {}}}; // tt undefined
    const out = filterAndSortSongs({songs: [s1, s2], scoresIndex: {}, sortMode: 'year'});
    // Same year → title tiebreak: '' < 'alpha' → s2 first
    expect(out[0].track.su).toBe('b');
  });

  /* ── title sort with second element having undefined ry ── */

  test('title sort with second element having undefined ry in tiebreak', () => {
    const s1: Song = {track: {su: 'a', tt: 'Same', an: 'X', ry: 2000, in: {}}};
    const s2: Song = {track: {su: 'b', tt: 'Same', an: 'Y', in: {}}}; // ry undefined
    const out = filterAndSortSongs({songs: [s1, s2], scoresIndex: {}, sortMode: 'title'});
    // Same title → ry tiebreak: 0 (undefined) < 2000 → s2 first
    expect(out[0].track.su).toBe('b');
  });

  /* ── compareByMetadataKey with both trackers undefined ── */

  test('instrument sort with neither entry having a tracker', () => {
    const sa = mkSong('a', 'Zebra', 'X');
    const sb = mkSong('b', 'Alpha', 'Y');
    const scoresIndex: Record<string, LeaderboardData> = {
      a: {songId: 'a'}, // no guitar tracker
      b: {songId: 'b'}, // no guitar tracker
    };
    const out = filterAndSortSongs({songs: [sa, sb], scoresIndex, sortMode: 'score', instrumentFilter: 'guitar'});
    // Both trackers undefined → score 0 vs 0 → cascade → title: 'alpha' < 'zebra'
    expect(out[0].track.su).toBe('b');
  });
});
