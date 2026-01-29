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

  test('generates deterministic categories for fixed seed', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'Artist 1', 1999),
      mkSong('b', 'Song B', 'Artist 2', 2005),
      mkSong('c', 'Song C', 'Artist 3', 2014),
      mkSong('d', 'Song D', 'Artist 4', 1985),
      mkSong('e', 'Song E', 'Artist 5', 1977),
      mkSong('f', 'Song F', 'Artist 6', 2020),
    ];

    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 98.5, fc: false, stars: 5}),
      b: mkScores('b', {pct: 99.4, fc: false, stars: 5}),
      c: mkScores('c', {pct: 92.0, fc: false, stars: 3}),
      d: mkScores('d', {pct: 99.0, fc: true, stars: 6}),
      e: mkScores('e', {pct: 98.1, fc: false, stars: 4}),
      f: mkScores('f', {pct: 98.9, fc: false, stars: 5}),
    };

    // Keep displayCount small so decade-variants have remaining candidates.
    const gen1 = new SuggestionGenerator({seed: 123, disableSkipping: true, fixedDisplayCount: 1});
    const out1 = gen1.generate(songs, scoresIndex);

    const gen2 = new SuggestionGenerator({seed: 123, disableSkipping: true, fixedDisplayCount: 1});
    const out2 = gen2.generate(songs, scoresIndex);

    expect(out1.map(c => c.key)).toEqual(out2.map(c => c.key));
    expect(out1.length).toBeGreaterThan(0);
    // No duplicates within a category
    for (const cat of out1) {
      const ids = cat.songs.map(s => s.songId);
      expect(new Set(ids).size).toBe(ids.length);
    }

    // Also ensure we don't repeat the same song across emitted categories
    const all = out1.flatMap(c => c.songs.map(s => s.songId));
    expect(new Set(all).size).toBe(all.length);
  });

  test('skip streak forces emission after two skips', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'Artist 1', 2001),
      mkSong('b', 'Song B', 'Artist 2', 2002),
      mkSong('c', 'Song C', 'Artist 3', 2003),
    ];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 98.5, fc: false, stars: 5}),
      b: mkScores('b', {pct: 98.6, fc: false, stars: 5}),
      c: mkScores('c', {pct: 98.7, fc: false, stars: 5}),
    };

    const rng = {
      nextInt: (_max: number) => 0,
      nextDouble: () => 0.99, // always "skip" unless forced
    };

    const gen = new SuggestionGenerator({rng, disableSkipping: false, fixedDisplayCount: 1});
    const run1 = gen.generate(songs, scoresIndex);
    const run2 = gen.generate(songs, scoresIndex);
    const run3 = gen.generate(songs, scoresIndex);

    expect(run1.length).toBe(0);
    expect(run2.length).toBe(0);
    expect(run3.length).toBeGreaterThan(0);
  });

  test('artist sampler skips duplicate artists and session-shown songs', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'SameArtist', 2001),
      mkSong('b', 'Song B', 'SameArtist', 2001),
      mkSong('c', 'Song C', 'OtherArtist', 2001),
      mkSong('d', 'Song D', 'ThirdArtist', 2001),
    ];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 98.9, fc: false, stars: 5}),
      b: mkScores('b', {pct: 98.8, fc: false, stars: 5}),
      c: mkScores('c', {pct: 10.0, fc: false, stars: 1}),
      d: mkScores('d', {pct: 10.0, fc: false, stars: 1}),
    };

    const rng = {
      nextInt: (_max: number) => 0,
      nextDouble: () => 0, // always emit when allowed
    };

    const gen = new SuggestionGenerator({rng, disableSkipping: false, fixedDisplayCount: 3});
    const out = gen.generate(songs, scoresIndex);
    const artistSampler = out.find(c => c.key === 'artist_sampler');
    expect(artistSampler).toBeTruthy();
    const items = artistSampler!.songs;
    const artists = items.map(i => i.artist.toLowerCase());
    expect(new Set(artists).size).toBe(artists.length);
  });

  test('artist sampler hits duplicate-artist continue branch', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'SameArtist', 2001),
      mkSong('b', 'Song B', 'SameArtist', 2001),
      mkSong('c', 'Song C', 'OtherArtist', 2001),
    ];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 10.0, fc: false, stars: 1}),
      b: mkScores('b', {pct: 10.0, fc: false, stars: 1}),
      c: mkScores('c', {pct: 10.0, fc: false, stars: 1}),
    };

    // Keep shuffle stable so duplicates stay adjacent.
    const rng = {
      nextInt: (maxExclusive: number) => Math.max(0, maxExclusive - 1),
      nextDouble: () => 0, // emit
    };

    const gen = new SuggestionGenerator({rng, disableSkipping: false, fixedDisplayCount: 3});
    const out = gen.generate(songs, scoresIndex);
    const artistSampler = out.find(c => c.key === 'artist_sampler');
    expect(artistSampler).toBeTruthy();
  });

  test('artist sampler skips usedArtists.add when artist is blank', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', '', 2001),
      mkSong('b', 'Song B', 'Artist 2', 2001),
    ];

    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 10.0, fc: false, stars: 1}),
      b: mkScores('b', {pct: 10.0, fc: false, stars: 1}),
    };

    const rng = {
      nextInt: (maxExclusive: number) => Math.max(0, maxExclusive - 1),
      nextDouble: () => 0,
    };

    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 1});
    const out = gen.generate(songs, scoresIndex);
    expect(out.some(c => c.key === 'artist_sampler')).toBe(true);
  });

  test('recent-song/artist fallback paths are exercised', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'Artist 1', 2001),
      mkSong('b', 'Song B', 'Artist 2', 2001),
      mkSong('c', 'Song C', 'Artist 3', 2001),
    ];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 98.9, fc: false, stars: 5}),
      b: mkScores('b', {pct: 98.8, fc: false, stars: 5}),
      c: mkScores('c', {pct: 98.7, fc: false, stars: 5}),
    };

    const rng = {
      nextInt: (_max: number) => 0,
      nextDouble: () => 0,
    };

    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 10});
    const first = gen.generate(songs, scoresIndex);
    expect(first.length).toBeGreaterThan(0);
    // Second run won't emit much (sessionShownSongs is full), but it will still run the recent-filter logic.
    const second = gen.generate(songs, scoresIndex);
    expect(Array.isArray(second)).toBe(true);
  });

  test('emits unplayed_all when scores are missing', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'Artist 1', 2001),
      mkSong('b', 'Song B', 'Artist 2', 2001),
      mkSong('c', 'Song C', 'Artist 3', 2001),
    ];
    const scoresIndex: Record<string, LeaderboardData | undefined> = {};

    const gen = new SuggestionGenerator({seed: 1, disableSkipping: true, fixedDisplayCount: 2});
    const out = gen.generate(songs, scoresIndex);
    expect(out.some(c => c.key === 'unplayed_all')).toBe(true);
  });

  test('uses random display count when fixedDisplayCount is not set', () => {
    const songs: Song[] = Array.from({length: 12}).map((_, i) => mkSong(`s${i}`, `Song ${i}`, `Artist ${i}`, 2001));
    const scoresIndex: Record<string, LeaderboardData> = Object.fromEntries(
      songs.map(s => [s.track.su, mkScores(s.track.su, {pct: 99.0, fc: false, stars: 5})]),
    );

    const gen = new SuggestionGenerator({seed: 42, disableSkipping: true});
    const out = gen.generate(songs, scoresIndex);
    expect(out.length).toBeGreaterThan(0);
    const first = out[0];
    expect(first.songs.length).toBeGreaterThanOrEqual(2);
    expect(first.songs.length).toBeLessThanOrEqual(5);
  });

  test('decade labeling handles 2000s and decade grouping works', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'Artist 1', 2000),
      mkSong('b', 'Song B', 'Artist 2', 2001),
      mkSong('c', 'Song C', 'Artist 3', 2010),
    ];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 99.0, fc: false, stars: 5}),
      b: mkScores('b', {pct: 99.0, fc: false, stars: 5}),
      c: mkScores('c', {pct: 99.0, fc: false, stars: 5}),
    };
    // Script RNG so early categories skip, but decade variant emits.
    // shouldEmit call order (with these inputs): fc_next, near_fc, almost_six, artist_sampler, decade(2000), decade(2010)
    const doubles = [0.99, 0.99, 0.99, 0.99, 0.0, 0.99];
    let i = 0;
    const rng = {
      nextInt: (_max: number) => 0,
      nextDouble: () => doubles[Math.min(i++, doubles.length - 1)],
    };
    const gen = new SuggestionGenerator({rng, disableSkipping: false, fixedDisplayCount: 1});

    const out = gen.generate(songs, scoresIndex);

    const decadeCats = out.filter(c => c.key.startsWith('fc_next_decade_'));
    expect(decadeCats.length).toBeGreaterThan(0);
    expect(decadeCats.some(c => c.title.includes("00's"))).toBe(true);
  });

  test('recent filters fall back when everything is recent', () => {
    const songs: Song[] = [
      mkSong('a', 'Song A', 'Artist 1', 1999),
      mkSong('b', 'Song B', 'Artist 2', 1999),
    ];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 99.0, fc: false, stars: 5}),
      b: mkScores('b', {pct: 99.0, fc: false, stars: 5}),
    };

    const rng = {nextInt: (_max: number) => 0, nextDouble: () => 0};
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 2});
    const out1 = gen.generate(songs, scoresIndex);
    expect(out1.length).toBeGreaterThan(0);
    // Second run: everything is "recent"; internal avoidRecent* should take fallback path.
    const out2 = gen.generate(songs, scoresIndex);
    expect(Array.isArray(out2)).toBe(true);
  });

  test('fixedDisplayCount clamps to at least 1', () => {
    const songs: Song[] = [mkSong('a', 'Song A', 'Artist 1', 2001)];
    const scoresIndex: Record<string, LeaderboardData> = {
      a: mkScores('a', {pct: 99.0, fc: false, stars: 5}),
    };

    const gen = new SuggestionGenerator({seed: 1, disableSkipping: true, fixedDisplayCount: 0});
    const out = gen.generate(songs, scoresIndex);
    expect(out.length).toBeGreaterThan(0);
    expect(out[0].songs.length).toBe(1);
  });

  test('pushRecent truncates recent lists when exceeding maximums', () => {
    const songs: Song[] = Array.from({length: 50}).map((_, i) =>
      mkSong(`s${i}`, `Song ${i}`, `Artist ${i}`, 2001),
    );

    // No scores => should emit unplayed_all with many picked songs.
    const rng = {nextInt: (_max: number) => 0, nextDouble: () => 0};
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 50});
    const out = gen.generate(songs, {});
    expect(out.some(c => c.key === 'unplayed_all')).toBe(true);
  });

  test('almost_six category executes pct undefined fallback branch', () => {
    const songs: Song[] = [mkSong('a', 'Song A', 'Artist 1', 2001)];
    const ld: LeaderboardData = {songId: 'a'} as any;
    const t = new ScoreTracker();
    t.initialized = true;
    t.numStars = 5;
    t.percentHit = 0; // pct() => undefined
    ld.guitar = t;
    const scoresIndex: Record<string, LeaderboardData> = {a: ld};

    const rng = {nextInt: (_max: number) => 0, nextDouble: () => 0};
    const gen = new SuggestionGenerator({rng, disableSkipping: true, fixedDisplayCount: 1});
    const out = gen.generate(songs, scoresIndex);
    expect(Array.isArray(out)).toBe(true);
  });

  test('shouldEmit hits high-candidate bucket (>=80) and still emits', () => {
    const songs: Song[] = Array.from({length: 81}).map((_, i) =>
      mkSong(`s${i}`,
        `Song ${i}`,
        `Artist ${i}`,
        // include invalid years so getDecadeStart invalid branch is exercised too
        i % 2 === 0 ? 1950 : 2010,
      ),
    );

    // Make early categories empty by keeping pct low but still marking guitar initialized
    const scoresIndex: Record<string, LeaderboardData> = Object.fromEntries(
      songs.map(s => [s.track.su, mkScores(s.track.su, {pct: 0.0, fc: false, stars: 0})]),
    );

    const rng = {
      nextInt: (_max: number) => 0,
      nextDouble: () => 0.99,
    };

    const gen = new SuggestionGenerator({rng, disableSkipping: false, fixedDisplayCount: 1});
    const out = gen.generate(songs, scoresIndex);

    expect(out.some(c => c.key === 'artist_sampler')).toBe(true);
  });

  test("toItem falls back to '(unknown)' title/artist when missing", () => {
    const song: Song = {
      track: {su: 'x', in: {}},
    } as any;

    const gen = new SuggestionGenerator({seed: 1, disableSkipping: true, fixedDisplayCount: 1});
    const out = gen.generate([song], {});

    const unplayed = out.find(c => c.key === 'unplayed_all');
    expect(unplayed).toBeTruthy();
    expect(unplayed!.songs[0].title).toBe('(unknown)');
    expect(unplayed!.songs[0].artist).toBe('(unknown)');
  });
});
