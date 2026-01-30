import type {LeaderboardData, Song} from '../models';
import {ScoreTracker} from '../models';
import {SuggestionGenerator} from '../suggestions/suggestionGenerator';

const mkSong = (id: string, title: string, artist: string, year?: number): Song => ({
  track: {su: id, tt: title, an: artist, ry: year, in: {}},
});

const mkScores = (songId: string, opts: {pct?: number; fc?: boolean; stars?: number}): LeaderboardData => {
  const t = new ScoreTracker();
  t.initialized = true;
  t.percentHit = Math.round((opts.pct ?? 0) * 10000);
  t.isFullCombo = opts.fc ?? false;
  t.numStars = opts.stars ?? 0;
  t.refreshDerived();
  return {songId, guitar: t};
};

describe('SuggestionGenerator', () => {
  test('constructor defaults are exercised', () => {
    const gen = new SuggestionGenerator();
    expect(gen).toBeTruthy();
  });

  test('getNext is deterministic for fixed seed', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'Artist 1', 2000),
      mkSong('b', 'Song B', 'Artist 2', 2001),
      mkSong('c', 'Song C', 'Artist 3', 2002),
      mkSong('d', 'Song D', 'Artist 4', 2003),
      mkSong('e', 'Song E', 'Artist 5', 2004),
      mkSong('f', 'Song F', 'Artist 6', 2005),
    ];

    const scoresIndex: Record<string, LeaderboardData> = {
      // Ensure some categories have candidates.
      a: mkScores('a', {pct: 95.0, fc: false, stars: 6}),
      b: mkScores('b', {pct: 96.0, fc: false, stars: 6}),
      c: mkScores('c', {pct: 92.0, fc: false, stars: 5}),
      d: mkScores('d', {pct: 90.0, fc: false, stars: 5}),
    };

    const gen1 = new SuggestionGenerator({seed: 123, disableSkipping: true, fixedDisplayCount: 2});
    const out1 = gen1.getNext(20, songs, scoresIndex);

    const gen2 = new SuggestionGenerator({seed: 123, disableSkipping: true, fixedDisplayCount: 2});
    const out2 = gen2.getNext(20, songs, scoresIndex);

    expect(out1.map(c => c.key)).toEqual(out2.map(c => c.key));
    expect(out1.length).toBeGreaterThan(0);
    for (const cat of out1) {
      const ids = cat.songs.map(s => s.songId);
      expect(new Set(ids).size).toBe(ids.length);
    }
  });

  test('emits unplayed_any when scores are missing', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'Artist 1', 2001),
      mkSong('b', 'Song B', 'Artist 2', 2001),
      mkSong('c', 'Song C', 'Artist 3', 2001),
    ];
    const scoresIndex: Record<string, LeaderboardData | undefined> = {};

    // Keep pipeline order stable to make the first-emitting category predictable.
    const rng = {
      nextInt: (maxExclusive: number) => Math.max(0, maxExclusive - 1),
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    const out = gen.getNext(1, songs, scoresIndex);
    expect(out.length).toBe(1);
    expect(out[0].key).toBe('first_plays_mixed');
  });

  test('uses random display count when fixedDisplayCount is not set', () => {
    const songs: Song[] = Array.from({length: 20}).map((_, i) => mkSong(`s${i}`, `Song ${i}`, `Artist ${i}`, 2001));
    const scoresIndex: Record<string, LeaderboardData> = Object.fromEntries(
      songs.map(s => [s.track.su, mkScores(s.track.su, {pct: 96.0, fc: false, stars: 6})]),
    );

    const gen = new SuggestionGenerator({seed: 42, disableSkipping: true});
    const out = gen.getNext(1, songs, scoresIndex);
    expect(out.length).toBe(1);
    expect(out[0].songs.length).toBeGreaterThanOrEqual(2);
    expect(out[0].songs.length).toBeLessThanOrEqual(5);
  });

  test('fixedDisplayCount clamps to at least 1', () => {
    const songs: Song[] = [mkSong('a', 'Song A', 'Artist 1', 2001)];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 96.0, fc: false, stars: 6}),
    };

    const gen = new SuggestionGenerator({seed: 1, disableSkipping: true, fixedDisplayCount: 0});
    const out = gen.getNext(50, songs, scoresIndex);
    expect(out.length).toBeGreaterThan(0);
    expect(out[0].songs.length).toBe(1);
  });

  test("song mapping falls back to '(unknown)' title/artist when missing", () => {
    const song: Song = {
      track: {su: 'x', in: {}},
    } as any;

    const rng = {
      nextInt: (maxExclusive: number) => Math.max(0, maxExclusive - 1),
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 1});
    const out = gen.getNext(1, [song], {});
    expect(out.length).toBe(1);
    expect(out[0].songs[0].title).toBe('(unknown)');
    expect(out[0].songs[0].artist).toBe('(unknown)');
  });

  test("decade labeling handles 2000s (00's)", () => {
    const songs: Song[] = Array.from({length: 40}).map((_, i) =>
      mkSong(`s${i}`, `Song ${i}`, `Artist ${i}`, 2000 + (i % 9)),
    );

    // Large eligible pool so decade variants still have fresh candidates after other pipelines run.
    const scoresIndex: Record<string, LeaderboardData> = Object.fromEntries(
      songs.map(s => [s.track.su, mkScores(s.track.su, {pct: 95.0, fc: false, stars: 6})]),
    );

    const gen = new SuggestionGenerator({seed: 2, disableSkipping: true, fixedDisplayCount: 2});
    const out = gen.getNext(500, songs, scoresIndex);
    const decadeCats = out.filter(c => c.key.startsWith('near_fc_any_decade_'));
    expect(decadeCats.length).toBeGreaterThan(0);
    expect(decadeCats.some(c => c.title.includes("00's"))).toBe(true);
  });

  test('resetForEndless produces more after exhaustion', () => {
    const songs: Song[] = Array.from({length: 60}).map((_, i) => mkSong(`s${i}`, `Song ${i}`, `Artist ${i % 10}`, 2000 + (i % 20)));
    const scoresIndex: Record<string, LeaderboardData> = Object.fromEntries(
      songs.map(s => [s.track.su, mkScores(s.track.su, {pct: 96.0, fc: false, stars: 6})]),
    );

    const gen = new SuggestionGenerator({seed: 7, disableSkipping: true, fixedDisplayCount: 2});

    // Drain pipelines.
    const out1 = gen.getNext(500, songs, scoresIndex);
    expect(out1.length).toBeGreaterThan(0);
    const out2 = gen.getNext(10, songs, scoresIndex);
    expect(out2.length).toBe(0);

    gen.resetForEndless();
    const out3 = gen.getNext(10, songs, scoresIndex);
    expect(out3.length).toBeGreaterThan(0);
  });

  test('shouldEmit forces emission after 2 skips (MAUI parity)', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0.99, // always fail probability checks
    };
    const gen = new SuggestionGenerator({rng});

    // Access private helper for focused heuristic coverage.
    const shouldEmit = (gen as any).shouldEmit as (key: string, candidateCount: number) => boolean;
    expect(shouldEmit.call(gen, 'x', 10)).toBe(false);
    expect(shouldEmit.call(gen, 'x', 10)).toBe(false);
    expect(shouldEmit.call(gen, 'x', 10)).toBe(true);
  });

  test('selectNewFirst prioritizes (session+category) novelty (MAUI parity)', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const a = mkSong('a', 'Song A', 'Artist 1', 2001);
    const b = mkSong('b', 'Song B', 'Artist 2', 2001);
    const c = mkSong('c', 'Song C', 'Artist 3', 2001);

    // Simulate prior state: song 'a' shown this session, song 'b' used in this category.
    (gen as any).sessionShownSongs.add('a');
    (gen as any).categorySongHistory.set('cat', new Set<string>(['b']));

    const selectNewFirst = (gen as any).selectNewFirst as (
      categoryKey: string,
      pool: Array<{song: Song; tracker: any}>,
      take: number,
    ) => Array<{song: Song; tracker: any}>;

    const picked = selectNewFirst.call(
      gen,
      'cat',
      [
        {song: a, tracker: null},
        {song: b, tracker: null},
        {song: c, tracker: null},
      ],
      2,
    );

    const ids = picked.map(p => p.song.track.su);
    // Must include the only (session+category) fresh pick.
    expect(ids).toContain('c');
    // Prefer non-category-used over category-used when there is room.
    expect(ids).toContain('a');
    expect(ids).not.toContain('b');
  });

  test('buildDecadeVariant formats title/desc for key categories (MAUI parity)', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const s0 = mkSong('s0', 'Song 0', 'Artist 0', 2000);
    const s1 = mkSong('s1', 'Song 1', 'Artist 1', 2001);
    const s2 = mkSong('s2', 'Song 2', 'Artist 2', 2002);

    const buildDecadeVariant = (gen as any).buildDecadeVariant as (
      baseKey: string,
      baseTitle: string,
      baseDescription: string,
      pool: Array<{song: Song; tracker: any}>,
    ) => Array<{key: string; title: string; description: string; songs: any[]}>;

    const pool = [s0, s1, s2].map(song => ({song, tracker: null}));

    const moreStars = buildDecadeVariant.call(gen, 'more_stars', 'Push These to Gold Stars', 'desc', pool);
    expect(moreStars[0].title).toContain("00's");
    expect(moreStars[0].title).toContain('Gold Stars');

    const unfcG = buildDecadeVariant.call(gen, 'unfc_guitar', 'Finish the Guitar FCs', 'desc', pool);
    expect(unfcG[0].title).toBe("Close Guitar FCs on Songs From the 00's");

    const unplayedAny = buildDecadeVariant.call(gen, 'unplayed_any', 'Try Something New', 'desc', pool);
    expect(unplayedAny[0].title).toBe("First Plays from the 00's");
    expect(unplayedAny[0].description).toBe("Unplayed songs from the 00's.");

    const unplayedG = buildDecadeVariant.call(gen, 'unplayed_guitar', 'New on Guitar', 'desc', pool);
    expect(unplayedG[0].title).toBe("First Guitar Plays (00's)");
    expect(unplayedG[0].description).toBe("Unplayed Guitar songs from the 00's.");

    const mixed = buildDecadeVariant.call(gen, 'first_plays_mixed', 'First Plays (Mixed)', 'desc', pool);
    expect(mixed[0].title).toBe("First Plays (Mixed 00's)");

    const nearRelaxed = buildDecadeVariant.call(gen, 'near_fc_relaxed', 'Close to FC (92%+)', 'desc', pool);
    expect(nearRelaxed[0].title).toBe("Close to FC (92%+) - 00's");

    const nearAny = buildDecadeVariant.call(gen, 'near_fc_any', 'FC These Next!', 'desc', pool);
    expect(nearAny[0].title).toBe("FC These Next! (00's)");

    const almostSix = buildDecadeVariant.call(gen, 'almost_six_star', 'Push to Gold Stars', 'desc', pool);
    expect(almostSix[0].title).toBe("Push 00's Songs to Gold Stars");

    const gains = buildDecadeVariant.call(gen, 'star_gains', 'Easy Star Gains', 'desc', pool);
    expect(gains[0].title).toBe("Easy Star Gains (00's)");
  });

  test('buildDecadeVariant returns empty when decade groups are not eligible', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };

    const build = (gen: SuggestionGenerator, pool: Array<{song: Song; tracker: any}>) => {
      const buildDecadeVariant = (gen as any).buildDecadeVariant as (
        baseKey: string,
        baseTitle: string,
        baseDescription: string,
        pool: Array<{song: Song; tracker: any}>,
      ) => any[];
      return buildDecadeVariant.call(gen, 'x', 't', 'd', pool);
    };

    // Invalid years filtered out.
    const gen1 = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    const invalid = [mkSong('a', 'A', 'AA', undefined), mkSong('b', 'B', 'BB', 0)].map(song => ({song, tracker: null}));
    expect(build(gen1, invalid)).toEqual([]);

    // Only 1 song per decade => no decade group with >=2.
    const gen2 = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    const split = [mkSong('c', 'C', 'CC', 1999), mkSong('d', 'D', 'DD', 2001)].map(song => ({song, tracker: null}));
    expect(build(gen2, split)).toEqual([]);

    // Take=1 => selection length <2 => suppressed.
    const gen3 = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 1});
    const eligible = [mkSong('e', 'E', 'EE', 2001), mkSong('f', 'F', 'FF', 2002)].map(song => ({song, tracker: null}));
    expect(build(gen3, eligible)).toEqual([]);
  });

  test('shouldEmit returns false when disableSkipping and candidateCount=0', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true});
    const shouldEmit = (gen as any).shouldEmit as (key: string, candidateCount: number) => boolean;
    expect(shouldEmit.call(gen, 'x', 0)).toBe(false);
  });

  test('varietyPack description matches count (2/3/4)', () => {
    const mkSongs = (n: number): Song[] =>
      Array.from({length: n}).map((_, i) => mkSong(`s${i}`, `Song ${i}`, `Artist ${i}`, 2001));

    const run = (fixedDisplayCount: number): string => {
      const rng = {
        nextInt: (_maxExclusive: number) => 0,
        nextDouble: () => 0,
      };
      const songs = mkSongs(10);
      const scoresIndex: Record<string, LeaderboardData | undefined> = {};
      const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount});
      gen.setSource(songs, scoresIndex);
      const varietyPack = (gen as any).varietyPack as () => any[];
      const out = varietyPack.call(gen);
      expect(out.length).toBe(1);
      return out[0].description as string;
    };

    expect(run(2)).toBe('Two different artists for variety.');
    expect(run(3)).toBe('Three different artists for variety.');
    expect(run(4)).toBe('Four different artists for variety.');
  });

  test('artistSamplerRotating emits only for non-placeholder artist names', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };

    // Placeholder suppression (single-char artist)
    {
      const songs: Song[] = [
        mkSong('a1', 'T1', 'A', 2001),
        mkSong('a2', 'T2', 'A', 2001),
        mkSong('a3', 'T3', 'A', 2001),
      ];
      const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
      gen.setSource(songs, {});
      const fn = (gen as any).artistSamplerRotating as () => any[];
      expect(fn.call(gen)).toEqual([]);
    }

    // Normal emit
    {
      const songs: Song[] = [
        mkSong('b1', 'T1', 'Radiohead', 2001),
        mkSong('b2', 'T2', 'Radiohead', 2001),
        mkSong('b3', 'T3', 'Radiohead', 2001),
      ];
      const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
      gen.setSource(songs, {});
      const fn = (gen as any).artistSamplerRotating as () => any[];
      const out = fn.call(gen);
      expect(out.length).toBe(1);
      expect(out[0].key).toContain('artist_sampler_');
      expect(out[0].title).toContain('Essentials');
      expect(out[0].songs.length).toBeGreaterThanOrEqual(2);
    }
  });

  test('sameNameNearFc emits a same-title near-FC category', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('a', 'Same Title', 'Artist 1', 2001),
      mkSong('b', 'Same Title', 'Artist 2', 2001),
      mkSong('c', 'Other', 'Artist 3', 2001),
    ];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 90.0, fc: false, stars: 6}),
      b: mkScores('b', {pct: 91.0, fc: false, stars: 6}),
    };

    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, scoresIndex);
    const fn = (gen as any).sameNameNearFc as () => any[];
    const out = fn.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].key).toContain('samename_nearfc_');
    expect(out[0].songs.length).toBeGreaterThan(0);
  });

  test('instrumentLabel default is exercised via invalid instrument key', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [mkSong('a', 'Song A', 'Artist 1', 2001), mkSong('b', 'Song B', 'Artist 2', 2001)];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});

    const fn = (gen as any).unplayedInstrument as (instrument: any) => any[];
    const out = fn.call(gen, 'weird_instrument');
    expect(out.length).toBe(1);
    expect(out[0].title).toBe('New on weird_instrument');
  });

  test('eachTracker returns empty when referenced song is missing from catalog', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource([], {});

    const board = mkScores('missing', {pct: 96.0, fc: false, stars: 6});
    const eachTracker = (gen as any).eachTracker as (
      song: Song | undefined,
      board: LeaderboardData | undefined,
      predicate: (t: any, instrument: any) => boolean,
    ) => any[];
    const out = eachTracker.call(gen, undefined, board, () => true);
    expect(out).toEqual([]);
  });

  test('selectNewFirst clears category history when all songs already used', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const a = mkSong('a', 'Song A', 'Artist 1', 2001);
    const b = mkSong('b', 'Song B', 'Artist 2', 2001);
    (gen as any).categorySongHistory.set('cat_all_used', new Set<string>(['a', 'b']));

    const selectNewFirst = (gen as any).selectNewFirst as (
      categoryKey: string,
      pool: Array<{song: Song; tracker: any}>,
      take: number,
    ) => Array<{song: Song; tracker: any}>;

    const picked = selectNewFirst.call(
      gen,
      'cat_all_used',
      [
        {song: a, tracker: null},
        {song: b, tracker: null},
      ],
      2,
    );

    expect(picked.map(p => p.song.track.su).sort()).toEqual(['a', 'b']);
  });

  test('selectNewFirst can fall back to categoryNew when session has seen everything', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const a = mkSong('a', 'Song A', 'Artist 1', 2001);
    const b = mkSong('b', 'Song B', 'Artist 2', 2001);
    (gen as any).sessionShownSongs.add('a');
    (gen as any).sessionShownSongs.add('b');

    const selectNewFirst = (gen as any).selectNewFirst as (
      categoryKey: string,
      pool: Array<{song: Song; tracker: any}>,
      take: number,
    ) => Array<{song: Song; tracker: any}>;

    const picked = selectNewFirst.call(
      gen,
      'cat_session_seen',
      [
        {song: a, tracker: null},
        {song: b, tracker: null},
      ],
      2,
    );

    expect(picked.map(p => p.song.track.su).sort()).toEqual(['a', 'b']);
  });

  test('selectNewFirst can fall back to oldOnes when only category-used songs exist', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const a = mkSong('a', 'Song A', 'Artist 1', 2001);
    const b = mkSong('b', 'Song B', 'Artist 2', 2001);
    // Mark everything as seen this session so freshNew is empty.
    (gen as any).sessionShownSongs.add('a');
    (gen as any).sessionShownSongs.add('b');
    // Mark only 'a' as category-used so categoryNew can include 'b' and oldOnes can include 'a'.
    (gen as any).categorySongHistory.set('cat_old', new Set<string>(['a']));

    const selectNewFirst = (gen as any).selectNewFirst as (
      categoryKey: string,
      pool: Array<{song: Song; tracker: any}>,
      take: number,
    ) => Array<{song: Song; tracker: any}>;

    const picked = selectNewFirst.call(
      gen,
      'cat_old',
      [
        {song: a, tracker: null},
        {song: b, tracker: null},
      ],
      2,
    );
    // Must include the oldOne ('a') after categoryNew is consumed.
    expect(picked.map(p => p.song.track.su).sort()).toEqual(['a', 'b']);
  });

  test('decade wrapper strategies execute and emit categories', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('fc1', 'FC1', 'Artist A', 2001),
      mkSong('fc2', 'FC2', 'Artist B', 2002),
      mkSong('r1', 'R1', 'Artist C', 2001),
      mkSong('r2', 'R2', 'Artist D', 2002),
      mkSong('a1', 'A1', 'Artist E', 2001),
      mkSong('a2', 'A2', 'Artist F', 2002),
      mkSong('sg1', 'SG1', 'Artist G', 2001),
      mkSong('sg2', 'SG2', 'Artist H', 2002),
      mkSong('u1', 'U1', 'Artist I', 2001),
      mkSong('u2', 'U2', 'Artist J', 2002),
      mkSong('ms1', 'MS1', 'Artist K', 2001),
      mkSong('ms2', 'MS2', 'Artist L', 2002),
      mkSong('up0', 'UP0', 'Artist M', 2001),
      mkSong('up1', 'UP1', 'Artist N', 2002),
      mkSong('upg1', 'UPG1', 'Artist O', 2001),
      mkSong('upg2', 'UPG2', 'Artist P', 2002),
    ];

    const scoresIndex: Record<string, LeaderboardData> = {
      // near_fc_any
      fc1: mkScores('fc1', {pct: 95.0, fc: false, stars: 6}),
      fc2: mkScores('fc2', {pct: 96.0, fc: false, stars: 6}),

      // near_fc_relaxed
      r1: mkScores('r1', {pct: 92.0, fc: false, stars: 5}),
      r2: mkScores('r2', {pct: 93.0, fc: false, stars: 5}),

      // almost_six_star
      a1: mkScores('a1', {pct: 90.0, fc: false, stars: 5}),
      a2: mkScores('a2', {pct: 91.0, fc: false, stars: 5}),

      // star_gains
      sg1: mkScores('sg1', {pct: 80.0, fc: false, stars: 4}),
      sg2: mkScores('sg2', {pct: 81.0, fc: false, stars: 4}),

      // unfc_guitar
      u1: mkScores('u1', {pct: 70.0, fc: false, stars: 6}),
      u2: mkScores('u2', {pct: 71.0, fc: false, stars: 6}),

      // more_stars
      ms1: mkScores('ms1', {pct: 50.0, fc: false, stars: 2}),
      ms2: mkScores('ms2', {pct: 51.0, fc: false, stars: 2}),

      // unplayed_guitar (stars=0)
      upg1: mkScores('upg1', {pct: 0, fc: false, stars: 0}),
      upg2: mkScores('upg2', {pct: 0, fc: false, stars: 0}),
    };

    const callFresh = (invoker: (gen: SuggestionGenerator) => any[]): any[] => {
      const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
      gen.setSource(songs, scoresIndex);
      return invoker(gen);
    };

    const keys = [
      ...callFresh(gen => (gen as any).fcTheseNextDecade.call(gen)).map((c: any) => c.key),
      ...callFresh(gen => (gen as any).nearFcRelaxedDecade.call(gen)).map((c: any) => c.key),
      ...callFresh(gen => (gen as any).almostSixStarsDecade.call(gen)).map((c: any) => c.key),
      ...callFresh(gen => (gen as any).starGainsDecade.call(gen)).map((c: any) => c.key),
      ...callFresh(gen => (gen as any).unFcInstrumentDecade.call(gen, 'guitar')).map((c: any) => c.key),
      ...callFresh(gen => (gen as any).getMoreStarsDecade.call(gen)).map((c: any) => c.key),
      ...callFresh(gen => (gen as any).unplayedAllDecade.call(gen)).map((c: any) => c.key),
      ...callFresh(gen => (gen as any).unplayedInstrumentDecade.call(gen, 'guitar')).map((c: any) => c.key),
      ...callFresh(gen => (gen as any).firstPlaysMixedDecade.call(gen)).map((c: any) => c.key),
    ];
    expect(keys.some(k => String(k).startsWith('near_fc_any_decade_'))).toBe(true);
    expect(keys.some(k => String(k).startsWith('near_fc_relaxed_decade_'))).toBe(true);
    expect(keys.some(k => String(k).startsWith('almost_six_star_decade_'))).toBe(true);
    expect(keys.some(k => String(k).startsWith('star_gains_decade_'))).toBe(true);
    expect(keys.some(k => String(k).startsWith('unfc_guitar_decade_'))).toBe(true);
    expect(keys.some(k => String(k).startsWith('more_stars_decade_'))).toBe(true);
    expect(keys.some(k => String(k).startsWith('unplayed_any_decade_'))).toBe(true);
    expect(keys.some(k => String(k).startsWith('unplayed_guitar_decade_'))).toBe(true);
    expect(keys.some(k => String(k).startsWith('first_plays_mixed_decade_'))).toBe(true);
  });

  test('sameNameSets and artistFocusUnplayed early-return paths are exercised', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };

    // sameNameSets: no duplicate titles => empty
    {
      const songs: Song[] = [mkSong('a', 'Unique A', 'Artist 1', 2001), mkSong('b', 'Unique B', 'Artist 2', 2001)];
      const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
      gen.setSource(songs, {});
      const fn = (gen as any).sameNameSets as () => any[];
      expect(fn.call(gen)).toEqual([]);
    }

    // artistFocusUnplayed: no unplayed songs => empty
    {
      const songs: Song[] = [mkSong('a', 'A', 'Artist 1', 2001)];
      const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
      gen.setSource(songs, {a: mkScores('a', {pct: 80, fc: false, stars: 4})});
      const fn = (gen as any).artistFocusUnplayed as () => any[];
      expect(fn.call(gen)).toEqual([]);
    }
  });

  test('sameNameSets emits a category when duplicate titles exist', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('a', 'Same Title', 'Artist 1', 2001),
      mkSong('b', 'Same Title', 'Artist 2', 2001),
      mkSong('c', 'Other', 'Artist 3', 2001),
    ];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const fn = (gen as any).sameNameSets as () => any[];
    const out = fn.call(gen);
    expect(out.length).toBe(1);
    expect(String(out[0].key)).toContain('samename_');
    expect(out[0].songs.length).toBeGreaterThan(0);
  });

  test('unplayedAll hits final-length==0 branch when shouldEmit is true but list is empty', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0, // ensure shouldEmit=true even for candidateCount=0
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: false, fixedDisplayCount: 2});
    gen.setSource([], {});
    const fn = (gen as any).unplayedAll as () => any[];
    expect(fn.call(gen)).toEqual([]);
  });

  test('unplayedAll emits a category when there are unplayed songs', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [mkSong('a', 'A', 'Artist 1', 2001), mkSong('b', 'B', 'Artist 2', 2002)];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const fn = (gen as any).unplayedAll as () => any[];
    const out = fn.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].key).toBe('unplayed_any');
    expect(out[0].songs.length).toBe(2);
  });

  test('strategy methods execute: almostSixStars / starGains / getMoreStars', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('a1', 'A1', 'Artist A', 2001),
      mkSong('a2', 'A2', 'Artist B', 2002),
      mkSong('sg1', 'SG1', 'Artist C', 2001),
      mkSong('sg2', 'SG2', 'Artist D', 2002),
      mkSong('ms1', 'MS1', 'Artist E', 2001),
      mkSong('ms2', 'MS2', 'Artist F', 2002),
    ];
    const scoresIndex: Record<string, LeaderboardData> = {
      a1: mkScores('a1', {pct: 90.0, fc: false, stars: 5}),
      a2: mkScores('a2', {pct: 91.0, fc: false, stars: 5}),
      sg1: mkScores('sg1', {pct: 50.0, fc: false, stars: 4}),
      sg2: mkScores('sg2', {pct: 51.0, fc: false, stars: 4}),
      ms1: mkScores('ms1', {pct: 10.0, fc: false, stars: 2}),
      ms2: mkScores('ms2', {pct: 11.0, fc: false, stars: 2}),
    };

    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, scoresIndex);

    const almost = (gen as any).almostSixStars.call(gen);
    const gains = (gen as any).starGains.call(gen);
    const more = (gen as any).getMoreStars.call(gen);

    expect(almost[0].key).toBe('almost_six_star');
    expect(gains[0].key).toBe('star_gains');
    expect(more[0].key).toBe('more_stars');
  });

  test('getMoreStars skips undefined leaderboard entries', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [mkSong('a', 'A', 'Artist 1', 2001)];
    const scoresIndex: Record<string, LeaderboardData | undefined> = {
      a: mkScores('a', {pct: 10.0, fc: false, stars: 2}),
      missing: undefined,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, scoresIndex);
    const out = (gen as any).getMoreStars.call(gen);
    expect(out.length).toBe(1);
  });

  test('unplayedInstrument excludes songs with a non-zero score for that instrument', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [mkSong('a', 'A', 'Artist 1', 2001)];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 80.0, fc: false, stars: 3}),
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, scoresIndex);
    const out = (gen as any).unplayedInstrument.call(gen, 'guitar');
    expect(out).toEqual([]);
  });

  test('artistSamplerRotating truncates when more than 5 picks exist', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = Array.from({length: 8}).map((_, i) => mkSong(`s${i}`, `T${i}`, 'The Band', 2001));
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const out = (gen as any).artistSamplerRotating.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].songs.length).toBe(2);
  });

  test('varietyPack returns empty when display size < 2', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = Array.from({length: 6}).map((_, i) => mkSong(`s${i}`, `Song ${i}`, `Artist ${i}`, 2001));
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 1});
    gen.setSource(songs, {});
    const out = (gen as any).varietyPack.call(gen);
    expect(out).toEqual([]);
  });

  test('varietyPack uses the 5-song default description when size is 5', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = Array.from({length: 10}).map((_, i) => mkSong(`s${i}`, `Song ${i}`, `Artist ${i}`, 2001));
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 5});
    gen.setSource(songs, {});
    const out = (gen as any).varietyPack.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].description).toBe('Five different artists for variety.');
  });

  test('firstPlaysMixed returns empty when all instruments have been played', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const mkAllPlayedBoard = (songId: string): LeaderboardData => {
      const mkT = () => {
        const t = new ScoreTracker();
        t.initialized = true;
        t.percentHit = 800000;
        t.isFullCombo = false;
        t.numStars = 3;
        t.refreshDerived();
        return t;
      };
      return {
        songId,
        guitar: mkT(),
        bass: mkT(),
        drums: mkT(),
        vocals: mkT(),
        pro_guitar: mkT(),
        pro_bass: mkT(),
      };
    };

    const songs: Song[] = [mkSong('a', 'A', 'Artist 1', 2001), mkSong('b', 'B', 'Artist 2', 2001)];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkAllPlayedBoard('a'),
      b: mkAllPlayedBoard('b'),
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, scoresIndex);
    const out = (gen as any).firstPlaysMixed.call(gen);
    expect(out).toEqual([]);
  });

  test('artistFocusUnplayed emits and uses Unknown Artist when missing', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      {track: {su: 'a', tt: 'A', ry: 2001, in: {}}} as any,
      {track: {su: 'b', tt: 'B', ry: 2001, in: {}}} as any,
    ];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const out = (gen as any).artistFocusUnplayed.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].title).toBe('Discover Unknown Artist');
  });

  test("sameNameSets uses _title when track.tt is missing", () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      {track: {su: 'a', an: 'Artist 1', ry: 2001, in: {}}, _title: 'Same'} as any,
      {track: {su: 'b', an: 'Artist 2', ry: 2001, in: {}}, _title: 'Same'} as any,
    ];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const out = (gen as any).sameNameSets.call(gen);
    expect(out.length).toBe(1);
    expect(String(out[0].key)).toContain('samename_Same');
  });

  test('sameNameNearFc returns empty when there are no duplicate title groups', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [mkSong('a', 'A', 'Artist 1', 2001), mkSong('b', 'B', 'Artist 2', 2001)];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 90.0, fc: false, stars: 6}),
      b: mkScores('b', {pct: 90.0, fc: false, stars: 6}),
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, scoresIndex);
    const out = (gen as any).sameNameNearFc.call(gen);
    expect(out).toEqual([]);
  });

  test('helper branches: pct/stars/getDecadeStart', () => {
    const gen = new SuggestionGenerator({seed: 1, disableSkipping: true, fixedDisplayCount: 1});

    const song: Song = mkSong('a', 'A', 'Artist', 1960);
    const mapSong = (gen as any).mapSong as (pair: any) => any;

    // pct: tracker null
    expect(mapSong.call(gen, {song, tracker: null}).percent).toBeUndefined();

    // pct: tracker present but percentHit <= 0
    const t0 = new ScoreTracker();
    t0.initialized = true;
    t0.percentHit = 0;
    t0.isFullCombo = false;
    t0.numStars = 0;
    t0.refreshDerived();
    const mapped0 = mapSong.call(gen, {song, tracker: t0});
    expect(mapped0.percent).toBeUndefined();
    expect(mapped0.stars).toBeUndefined();

    // getDecadeStart invalid year (<1970) results in no decade variants.
    const buildDecadeVariant = (gen as any).buildDecadeVariant as (baseKey: string, baseTitle: string, baseDesc: string, pool: any[]) => any[];
    const out = buildDecadeVariant.call(gen, 'x', 't', 'd', [{song, tracker: null}, {song, tracker: null}]);
    expect(out).toEqual([]);
  });

  test('buildDecadeVariant filters out items with missing track', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    const good1 = mkSong('a', 'A', 'Artist 1', 2001);
    const good2 = mkSong('b', 'B', 'Artist 2', 2002);
    const bad: any = {track: undefined};

    const buildDecadeVariant = (gen as any).buildDecadeVariant as (baseKey: string, baseTitle: string, baseDesc: string, pool: any[]) => any[];
    const out = buildDecadeVariant.call(gen, 'star_gains', 'Easy Star Gains', 'desc', [
      {song: bad, tracker: null},
      {song: good1, tracker: null},
      {song: good2, tracker: null},
    ]);
    expect(out.length).toBe(1);
  });

  test('getMoreStarsDecade returns empty when shouldEmit is false (no candidates)', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource([], {});
    const out = (gen as any).getMoreStarsDecade.call(gen);
    expect(out).toEqual([]);
  });

  test('artistSamplerRotating returns empty when no artist has >=3 songs', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('a', 'A', 'Artist 1', 2001),
      mkSong('b', 'B', 'Artist 1', 2001),
      mkSong('c', 'C', 'Artist 2', 2001),
    ];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const out = (gen as any).artistSamplerRotating.call(gen);
    expect(out).toEqual([]);
  });

  test('sameNameNearFc returns empty when some duplicate-title songs have no board', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('a', 'Same', 'Artist 1', 2001),
      mkSong('b', 'Same', 'Artist 2', 2001),
    ];
    // Only one board present => group size becomes 1 (since songs without board are skipped)
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 90.0, fc: false, stars: 6}),
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, scoresIndex);
    const out = (gen as any).sameNameNearFc.call(gen);
    expect(out).toEqual([]);
  });

  test('getNext skips empty/duplicate categories (custom pipeline injection)', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    // Inject a tiny pipeline list to exercise getNext skipping logic.
    (gen as any).initialized = true;
    (gen as any).pipelines = [
      () => [
        {key: 'k1', title: 't', description: 'd', songs: []},
        {key: 'k2', title: 't', description: 'd', songs: [{songId: 'x', title: 'X', artist: 'A'}]},
        {key: 'k2', title: 't', description: 'd', songs: [{songId: 'y', title: 'Y', artist: 'B'}]},
      ],
    ];

    const out = gen.getNext(10);
    expect(out.map(c => c.key)).toEqual(['k2']);
  });

  test('selectNewFirst can finish from oldOnes (covers oldOnes break)', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const songOld = mkSong('old', 'Old', 'Artist', 2001);
    const songNew = mkSong('new', 'New', 'Artist', 2001);

    (gen as any).categorySongHistory.set('k', new Set(['old']));

    const res = (gen as any).selectNewFirst.call(
      gen,
      'k',
      [
        {song: songOld, tracker: null},
        {song: songNew, tracker: null},
      ],
      2,
    );

    expect(res.map((p: any) => p.song.track.su).sort()).toEqual(['new', 'old']);
  });

  test('selectNewFirst evaluates oldOnes break condition as false (multi-old)', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 3});

    const old1 = mkSong('old1', 'Old 1', 'Artist', 2001);
    const old2 = mkSong('old2', 'Old 2', 'Artist', 2001);
    const newSong = mkSong('new', 'New', 'Artist', 2001);

    (gen as any).categorySongHistory.set('k2', new Set(['old1', 'old2']));
    (gen as any).sessionShownSongs.add('new');

    const res = (gen as any).selectNewFirst.call(
      gen,
      'k2',
      [
        {song: old1, tracker: null},
        {song: old2, tracker: null},
        {song: newSong, tracker: null},
      ],
      3,
    );

    expect(res.map((p: any) => p.song.track.su).sort()).toEqual(['new', 'old1', 'old2']);
  });

  test('selectNewFirst suppresses duplicate songIds in freshNew', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const dup = mkSong('dup', 'Dup', 'Artist', 2001);
    const other = mkSong('other', 'Other', 'Artist', 2001);

    const res = (gen as any).selectNewFirst.call(
      gen,
      'fresh_dups',
      [
        {song: dup, tracker: null},
        {song: dup, tracker: null},
        {song: other, tracker: null},
      ],
      3,
    );

    const ids = res.map((p: any) => p.song.track.su);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids).toContain('dup');
    expect(ids).toContain('other');
  });

  test('selectNewFirst suppresses duplicate songIds in categoryNew', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const dup = mkSong('dup', 'Dup', 'Artist', 2001);
    const other = mkSong('other', 'Other', 'Artist', 2001);

    // Put dup into sessionShownSongs so it won't be freshNew; it becomes categoryNew.
    (gen as any).sessionShownSongs.add('dup');

    const res = (gen as any).selectNewFirst.call(
      gen,
      'category_dups',
      [
        {song: dup, tracker: null},
        {song: dup, tracker: null},
        {song: other, tracker: null},
      ],
      3,
    );

    const ids = res.map((p: any) => p.song.track.su);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids).toContain('dup');
    expect(ids).toContain('other');
  });

  test('selectNewFirst suppresses duplicate songIds in oldOnes', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const dup = mkSong('dup', 'Dup', 'Artist', 2001);
    const other = mkSong('other', 'Other', 'Artist', 2001);

    // Mark dup as used so it becomes oldOnes.
    (gen as any).categorySongHistory.set('old_dups', new Set(['dup']));

    const res = (gen as any).selectNewFirst.call(
      gen,
      'old_dups',
      [
        {song: dup, tracker: null},
        {song: dup, tracker: null},
        {song: other, tracker: null},
      ],
      3,
    );

    const ids = res.map((p: any) => p.song.track.su);
    expect(new Set(ids).size).toBe(ids.length);
    expect(ids).toContain('dup');
    expect(ids).toContain('other');
  });

  test('buildDecadeVariant skips out-of-range years and formats star_gains title', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});

    const sInvalid = mkSong('bad', 'Bad', 'Artist', 1965); // decade undefined => skipped
    const s1 = mkSong('a', 'A', 'Artist', 1981);
    const s2 = mkSong('b', 'B', 'Artist', 1982);
    gen.setSource([sInvalid, s1, s2], {});

    const out = (gen as any).buildDecadeVariant.call(
      gen,
      'star_gains',
      'Easy Star Gains',
      'Mid-star songs ripe for improvement.',
      [
        {song: sInvalid, tracker: null},
        {song: s1, tracker: mkScores('a', {pct: 0, fc: false, stars: 3}).guitar},
        {song: s2, tracker: mkScores('b', {pct: 0, fc: false, stars: 3}).guitar},
      ],
    );

    expect(out.length).toBe(1);
    expect(out[0].key).toBe('star_gains_decade_80');
    expect(out[0].title).toBe("Easy Star Gains (80's)");
  });

  test('buildDecadeVariant uses default title format for unknown baseKey', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    const s1 = mkSong('a', 'A', 'Artist', 1981);
    const s2 = mkSong('b', 'B', 'Artist', 1982);
    gen.setSource([s1, s2], {});

    const out = (gen as any).buildDecadeVariant.call(
      gen,
      'totally_unknown',
      'Base Title',
      'Base Desc.',
      [
        {song: s1, tracker: null},
        {song: s2, tracker: null},
      ],
    );

    expect(out.length).toBe(1);
    expect(out[0].title).toBe("Base Title (80's)");
  });

  test('getNext breaks cleanly if a pipeline slot is undefined', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    (gen as any).initialized = true;
    (gen as any).pipelines = [undefined as any];
    const out = gen.getNext(10);
    expect(out).toEqual([]);
  });

  test('getMoreStarsDecade skips undefined score entries', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    const songs: Song[] = [
      mkSong('b', 'B', 'Artist 1', 1981),
      mkSong('c', 'C', 'Artist 2', 1982),
    ];
    const scoresIndex: Record<string, LeaderboardData | undefined> = {
      missing: undefined,
      b: mkScores('b', {pct: 80.0, fc: false, stars: 2}),
      c: mkScores('c', {pct: 80.0, fc: false, stars: 2}),
    };
    gen.setSource(songs, scoresIndex);
    const out = (gen as any).getMoreStarsDecade.call(gen);
    expect(out.length).toBe(1);
  });

  test('unplayedInstrument returns empty when shouldEmit=true but pool is empty', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: false, fixedDisplayCount: 2});
    gen.setSource([], {});
    const out = (gen as any).unplayedInstrument.call(gen, 'guitar');
    expect(out).toEqual([]);
  });

  test('artistSamplerRotating slices down to display count when >5 picks', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = Array.from({length: 6}).map((_, i) => mkSong(`s${i}`, `Song ${i}`, 'Artist X', 2001));
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const out = (gen as any).artistSamplerRotating.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].songs.length).toBe(2);
  });

  test('artistSamplerRotating does not slice when <=5 picks', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('a', 'A', 'Artist Y', 2001),
      mkSong('b', 'B', 'Artist Y', 2001),
      mkSong('c', 'C', 'Artist Y', 2001),
    ];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const out = (gen as any).artistSamplerRotating.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].songs.length).toBe(3);
  });

  test('artistSamplerRotating suppresses placeholder-ish artist names', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('a', 'A', 'A', 2001),
      mkSong('b', 'B', 'A', 2001),
      mkSong('c', 'C', 'A', 2001),
    ];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const out = (gen as any).artistSamplerRotating.call(gen);
    expect(out).toEqual([]);
  });

  test('artistSamplerRotating falls back to group key when track.an is missing', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      {track: {su: 'a', tt: 'A', ry: 2001, in: {}}} as any,
      {track: {su: 'b', tt: 'B', ry: 2001, in: {}}} as any,
      {track: {su: 'c', tt: 'C', ry: 2001, in: {}}} as any,
    ];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource(songs, {});
    const out = (gen as any).artistSamplerRotating.call(gen);
    expect(out).toEqual([]);
  });

  test('varietyPack skips session-shown songs', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const songs: Song[] = [
      mkSong('a', 'A', 'Artist 1', 2001),
      mkSong('b', 'B', 'Artist 2', 2001),
      mkSong('c', 'C', 'Artist 3', 2001),
    ];
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 3});
    gen.setSource(songs, {});
    (gen as any).sessionShownSongs.add('a');
    const out = (gen as any).varietyPack.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].songs.map((s: any) => s.songId)).not.toContain('a');
  });

  test('sameNameNearFc uses _title fallback when track.tt is missing', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const s1: Song = {track: {su: 'a', an: 'Artist 1', ry: 2001, in: {}}, _title: 'Same'} as any;
    const s2: Song = {track: {su: 'b', an: 'Artist 2', ry: 2001, in: {}}, _title: 'Same'} as any;
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 90.0, fc: false, stars: 6}),
      b: mkScores('b', {pct: 90.0, fc: false, stars: 6}),
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource([s1, s2], scoresIndex);
    const out = (gen as any).sameNameNearFc.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].title).toBe("Close to FC: 'Same' Variants");
  });

  test('sameNameSets uses _title fallback when track.tt is missing', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const s1: Song = {track: {su: 'a', an: 'Artist 1', ry: 2001, in: {}}, _title: 'Same'} as any;
    const s2: Song = {track: {su: 'b', an: 'Artist 2', ry: 2001, in: {}}, _title: 'Same'} as any;
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource([s1, s2], {});
    const out = (gen as any).sameNameSets.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].title).toBe("Songs Named 'Same'");
  });

  test('sameNameSets falls back to empty title when both track.tt and _title are missing', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const s1: Song = {track: {su: 'a', an: 'Artist 1', ry: 2001, in: {}}} as any;
    const s2: Song = {track: {su: 'b', an: 'Artist 2', ry: 2001, in: {}}} as any;
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource([s1, s2], {});
    const out = (gen as any).sameNameSets.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].title).toBe("Songs Named ''");
  });

  test('sameNameNearFc falls back to empty title when both track.tt and _title are missing', () => {
    const rng = {
      nextInt: (_maxExclusive: number) => 0,
      nextDouble: () => 0,
    };
    const s1: Song = {track: {su: 'a', an: 'Artist 1', ry: 2001, in: {}}} as any;
    const s2: Song = {track: {su: 'b', an: 'Artist 2', ry: 2001, in: {}}} as any;
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 90.0, fc: false, stars: 6}),
      b: mkScores('b', {pct: 90.0, fc: false, stars: 6}),
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    gen.setSource([s1, s2], scoresIndex);
    const out = (gen as any).sameNameNearFc.call(gen);
    expect(out.length).toBe(1);
    expect(out[0].title).toBe("Close to FC: '' Variants");
  });

  test('generate helper delegates to getNext', () => {
    const gen = new SuggestionGenerator({seed: 1, disableSkipping: true, fixedDisplayCount: 2});
    const songs: Song[] = [mkSong('a', 'A', 'Artist 1', 2001), mkSong('b', 'B', 'Artist 2', 2001)];
    const scoresIndex: Record<string, LeaderboardData | undefined> = {};
    const out = gen.generate(songs, scoresIndex);
    expect(Array.isArray(out)).toBe(true);
  });
});
