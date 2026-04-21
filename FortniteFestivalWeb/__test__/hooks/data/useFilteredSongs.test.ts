import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useFilteredSongs } from '../../../src/hooks/data/useFilteredSongs';
import { compareByMode } from '../../../src/pages/songs/components/SongRow';
import type { ServerSong as Song, PlayerScore, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';

function song(id: string, title: string, artist: string, year?: number, maxScores?: Partial<Record<InstrumentKey, number>>): Song {
  return {
    songId: id,
    title,
    artist,
    year: year ?? 2024,
    albumArt: '',
    maxScores: maxScores ?? null,
    difficulty: {
      guitar: 3,
      bass: 3,
      drums: 3,
      vocals: 3,
      proGuitar: 3,
      proBass: 3,
      proVocals: 3,
      proDrums: 3,
      proCymbals: 3,
    },
  } as Song;
}

function score(songId: string, overrides: Partial<PlayerScore> = {}): PlayerScore {
  return { songId, instrument: 'guitar' as InstrumentKey, score: 1000, rank: 1, totalEntries: 100, isFullCombo: false, season: 1, stars: 3, ...overrides } as PlayerScore;
}

const emptyFilters = {
  missingScores: {}, hasScores: {}, missingFCs: {}, hasFCs: {},
  seasonFilter: {}, percentileFilter: {}, starsFilter: {}, difficultyFilter: {},
};

const songs = [
  song('s1', 'Alpha', 'Artist A', 2020),
  song('s2', 'Beta', 'Artist B', 2021),
  song('s3', 'Gamma', 'Artist C', 2022),
];

describe('useFilteredSongs', () => {
  it('returns all songs with no filters', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current).toHaveLength(3);
  });

  it('filters by search text (title)', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: 'alpha', sortMode: 'title' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current).toHaveLength(1);
    expect(result.current[0]!.title).toBe('Alpha');
  });

  it('filters by search text (artist)', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: 'artist b', sortMode: 'title' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current).toHaveLength(1);
    expect(result.current[0]!.title).toBe('Beta');
  });

  it('sorts by title ascending', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current.map(s => s.title)).toEqual(['Alpha', 'Beta', 'Gamma']);
  });

  it('sorts by title descending', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current.map(s => s.title)).toEqual(['Gamma', 'Beta', 'Alpha']);
  });

  it('sorts by artist', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'artist' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current[0]!.artist).toBe('Artist A');
  });

  it('sorts by year', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'year' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current.map(s => s.year)).toEqual([2020, 2021, 2022]);
  });

  it('sorts by duration', () => {
    const songsWithDur = [
      { ...songs[0], durationSeconds: 300 } as Song,
      { ...songs[1], durationSeconds: 180 } as Song,
      { ...songs[2], durationSeconds: 240 } as Song,
    ];
    const { result: asc } = renderHook(() => useFilteredSongs({
      songs: songsWithDur, search: '', sortMode: 'duration' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(asc.current.map(s => s.songId)).toEqual(['s2', 's3', 's1']);

    const { result: desc } = renderHook(() => useFilteredSongs({
      songs: songsWithDur, search: '', sortMode: 'duration' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(desc.current.map(s => s.songId)).toEqual(['s1', 's3', 's2']);
  });

  it('treats missing durationSeconds as 0 when sorting', () => {
    const songsMixed = [
      { ...songs[0], durationSeconds: 200 } as Song,
      { ...songs[1] } as Song, // no durationSeconds
      { ...songs[2], durationSeconds: 100 } as Song,
    ];
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsMixed, search: '', sortMode: 'duration' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    // s2 (undef → 0), then s3 (100), then s1 (200)
    expect(result.current.map(s => s.songId)).toEqual(['s2', 's3', 's1']);
  });

  it('applies season filter', () => {
    const scoreMap = new Map([['s1', score('s1', { season: 1 })], ['s2', score('s2', { season: 2 })], ['s3', score('s3', { season: 1 })]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, seasonFilter: { 1: true, 2: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.songId)).toEqual(['s1', 's3']);
  });

  it('applies stars filter', () => {
    const scoreMap = new Map([['s1', score('s1', { stars: 5 })], ['s2', score('s2', { stars: 3 })], ['s3', score('s3', { stars: 5 })]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, starsFilter: { 5: true, 3: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    expect(result.current).toHaveLength(2);
  });

  it('applies missing scores filter', () => {
    const scoreMap = new Map([['s1', score('s1')]]);
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1')]])]]);
    const filters = { ...emptyFilters, missingScores: { guitar: true } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    // s2 and s3 have no scores → missing
    expect(result.current.map(s => s.songId)).toEqual(['s2', 's3']);
  });

  it('returns empty for no match', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: 'zzzzz', sortMode: 'title' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current).toHaveLength(0);
  });

  it('applies percentile filter', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { rank: 1, totalEntries: 100 })],
      ['s2', score('s2', { rank: 50, totalEntries: 100 })],
      ['s3', score('s3', { rank: 90, totalEntries: 100 })],
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, percentileFilter: { 1: true, 50: false, 90: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.songId)).toEqual(['s1']);
  });

  it('applies difficulty filter', () => {
    const songsWithDiff = [
      { ...songs[0], difficulty: { guitar: 3 } },
      { ...songs[1], difficulty: { guitar: 5 } },
      { ...songs[2], difficulty: { guitar: 3 } },
    ];
    const scoreMap = new Map([['s1', score('s1')], ['s2', score('s2')], ['s3', score('s3')]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, difficultyFilter: { 4: true, 6: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithDiff as any, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    expect(result.current).toHaveLength(2);
  });

  it('applies hasScores filter', () => {
    const scoreMap = new Map([['s1', score('s1')]]);
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1')]])]]);
    const filters = { ...emptyFilters, hasScores: { guitar: true } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.songId)).toEqual(['s1']);
  });

  it('applies hasFCs filter', () => {
    const scoreMap = new Map([['s1', score('s1', { isFullCombo: true })], ['s2', score('s2', { isFullCombo: false })]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, hasFCs: { guitar: true } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.songId)).toEqual(['s1']);
  });

  it('applies missingFCs filter', () => {
    const scoreMap = new Map([['s1', score('s1', { isFullCombo: true })], ['s2', score('s2', { isFullCombo: false })]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, missingFCs: { guitar: true } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    // s2 has score but no FC, s3 has no score at all (missing FC by default)
    expect(result.current.map(s => s.songId)).toContain('s2');
    expect(result.current.map(s => s.songId)).toContain('s3');
  });

  it('filters with single instrument scope', () => {
    const scoreMap = new Map([['s1', score('s1')]]);
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1')]])]]);
    const filters = { ...emptyFilters, missingScores: { guitar: true } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.songId)).toEqual(['s2', 's3']);
  });

  it('uses default sort when scoreMap empty and sortMode is score', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'score' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    // Falls back to title sort
    expect(result.current[0]!.title).toBe('Alpha');
  });

  it('filters songs with no percentile data (bracket 0)', () => {
    const scoreMap = new Map([['s1', score('s1', { rank: 0, totalEntries: 0 })]]);
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1', { rank: 0, totalEntries: 0 })]])]]);
    const filters = { ...emptyFilters, percentileFilter: { 0: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    // s1 has rank 0 → bracket 0 → filtered out; s2 and s3 have no score → bracket 0 → filtered out
    expect(result.current).toHaveLength(0);
  });

  it('sorts by score mode with scoreMap data', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { score: 5000 })],
      ['s2', score('s2', { score: 9000 })],
      ['s3', score('s3', { score: 1000 })],
    ]);
    const allScoreMap = new Map(
      Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])])
    );
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'score' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.songId)).toEqual(['s2', 's1', 's3']);
  });

  it('filters by hasScores per instrument', () => {
    const scoreMap = new Map([['s1', score('s1')]]);
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1')]])]]);
    const filters = { ...emptyFilters, hasScores: { guitar: true } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    // Only s1 has a guitar score
    expect(result.current).toHaveLength(1);
    expect(result.current[0]!.songId).toBe('s1');
  });

  it('filters by season when season filter is active', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { season: 1 })],
      ['s2', score('s2', { season: 2 })],
      ['s3', score('s3', { season: 1 })],
    ]);
    const allScoreMap = new Map(
      Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])])
    );
    const filters = { ...emptyFilters, seasonFilter: { 1: false, 2: true } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    // Season 1 is filtered out, only s2 (season 2) should remain
    expect(result.current).toHaveLength(1);
    expect(result.current[0]!.songId).toBe('s2');
  });

  it('skips filter instruments with false entries', () => {
    const scoreMap = new Map([['s1', score('s1')]]);
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1')]])]]);
    const filters = {
      missingScores: { guitar: true, bass: false },
      missingFCs: { guitar: false },
      hasScores: { bass: false },
      hasFCs: { guitar: false },
      seasonFilter: {}, percentileFilter: {}, starsFilter: {}, difficultyFilter: {},
    };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    // Only missingScores guitar=true is active; s2 and s3 have no guitar score
    expect(result.current.map(s => s.songId)).toEqual(['s2', 's3']);
  });

  it('passes songs with null percentile when bracket 0 is not filtered', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { rank: 0, totalEntries: 0 })],
      ['s2', score('s2', { rank: 5, totalEntries: 100 })],
    ]);
    const allScoreMap = new Map(
      Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])])
    );
    const filters = { ...emptyFilters, percentileFilter: { 5: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    // s1 has null pct, bracket 0 not filtered → passes
    // s2 has pct=5%, bracket 5 is filtered out → excluded
    // s3 has no score, bracket 0 not filtered → passes
    expect(result.current.map(s => s.songId)).toContain('s1');
    expect(result.current.map(s => s.songId)).toContain('s3');
    expect(result.current.map(s => s.songId)).not.toContain('s2');
  });

  it('sorts by year with undefined year using fallback', () => {
    const songsWithUndef = [
      song('s1', 'Alpha', 'Artist A', 2020),
      { ...song('s2', 'Beta', 'Artist B'), year: undefined } as unknown as Song,
      song('s3', 'Gamma', 'Artist C', 2022),
    ];
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithUndef, search: '', sortMode: 'year' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    // undefined year → 0 via ?? fallback, sorts first
    expect(result.current[0]!.songId).toBe('s2');
  });
});

describe('compareByMode — branch coverage', () => {
  const a = score('s1', { score: 100, accuracy: 900, isFullCombo: false, rank: 5, totalEntries: 100, stars: 4, season: 3 });
  const b = score('s2', { score: 200, accuracy: 950, isFullCombo: true, rank: 2, totalEntries: 100, stars: 5, season: 5 });

  it('score: compares by score', () => {
    expect(compareByMode('score' as any, a, b)).toBeLessThan(0);
  });

  it('percentage: compares by accuracy then FC tiebreaker', () => {
    expect(compareByMode('percentage' as any, a, b)).toBeLessThan(0);
    const x = score('s1', { accuracy: 900, isFullCombo: false });
    const y = score('s2', { accuracy: 900, isFullCombo: true });
    expect(compareByMode('percentage' as any, x, y)).toBeLessThan(0);
  });

  it('percentile: compares by rank/totalEntries ratio', () => {
    expect(compareByMode('percentile' as any, a, b)).toBeGreaterThan(0);
  });

  it('percentile: Infinity fallback when rank=0 or totalEntries=0', () => {
    const noRank = score('s1', { rank: 0, totalEntries: 0 });
    expect(compareByMode('percentile' as any, noRank, b)).toBeGreaterThan(0);
    expect(compareByMode('percentile' as any, a, noRank)).toBeLessThan(0);
  });

  it('percentile: Infinity fallback when only totalEntries=0', () => {
    const noTotal = score('s1', { totalEntries: 0 });
    expect(compareByMode('percentile' as any, noTotal, b)).toBeGreaterThan(0);
  });

  it('stars: compares by star count with null fallback', () => {
    expect(compareByMode('stars' as any, a, b)).toBeLessThan(0);
    const noStars = score('s1', { stars: undefined as any });
    expect(compareByMode('stars' as any, noStars, b)).toBeLessThan(0);
  });

  it('seasonachieved: compares by season with null fallback', () => {
    expect(compareByMode('seasonachieved' as any, a, b)).toBeLessThan(0);
    const noSeason = score('s1', { season: undefined as any });
    expect(compareByMode('seasonachieved' as any, noSeason, b)).toBeLessThan(0);
  });

  it('hasfc: compares by isFullCombo boolean', () => {
    expect(compareByMode('hasfc' as any, a, b)).toBeLessThan(0);
  });

  it('default: returns 0 for unknown mode', () => {
    expect(compareByMode('unknown' as any, a, b)).toBe(0);
  });

  it('undefined a sorts last', () => {
    expect(compareByMode('score' as any, undefined, b)).toBe(1);
  });

  it('undefined b sorts last', () => {
    expect(compareByMode('score' as any, a, undefined)).toBe(-1);
  });

  it('both undefined returns 0', () => {
    expect(compareByMode('score' as any, undefined, undefined)).toBe(0);
  });
});

describe('useFilteredSongs — additional sort modes', () => {
  it('sorts by percentile mode', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { rank: 1, totalEntries: 100 })],
      ['s2', score('s2', { rank: 50, totalEntries: 100 })],
    ]);
    const allScoreMap = new Map(
      Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])])
    );
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'percentile' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('s1');
    const idx2 = ids.indexOf('s2');
    expect(idx1).toBeLessThan(idx2);
  });

  it('sorts by stars mode', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { stars: 6 })],
      ['s2', score('s2', { stars: 3 })],
    ]);
    const allScoreMap = new Map(
      Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])])
    );
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'stars' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('s1');
    const idx2 = ids.indexOf('s2');
    expect(idx1).toBeLessThan(idx2);
  });

  it('sorts by seasonachieved mode', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { season: 5 })],
      ['s2', score('s2', { season: 3 })],
    ]);
    const allScoreMap = new Map(
      Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])])
    );
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'seasonachieved' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('s1');
    const idx2 = ids.indexOf('s2');
    expect(idx1).toBeLessThan(idx2);
  });

  it('sorts by hasfc mode', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { isFullCombo: true })],
      ['s2', score('s2', { isFullCombo: false })],
    ]);
    const allScoreMap = new Map(
      Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])])
    );
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'hasfc' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('s1');
    const idx2 = ids.indexOf('s2');
    expect(idx1).toBeLessThan(idx2);
  });

  it('sorts by percentage mode', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { accuracy: 900 })],
      ['s2', score('s2', { accuracy: 950 })],
    ]);
    const allScoreMap = new Map(
      Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])])
    );
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'percentage' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('s1');
    const idx2 = ids.indexOf('s2');
    expect(idx1).toBeLessThan(idx2);
  });

  it('sorts by intensity mode ascending using the selected instrument difficulty', () => {
    const songsWithIntensity = [
      { ...songs[0], difficulty: { guitar: 5 } },
      { ...songs[1], difficulty: { guitar: 1 } },
      { ...songs[2], difficulty: { guitar: 3 } },
    ] as Song[];

    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithIntensity,
      search: '',
      sortMode: 'intensity' as any,
      sortAscending: true,
      filters: emptyFilters as any,
      instrument: 'Solo_Guitar' as InstrumentKey,
      scoreMap: new Map(),
      allScoreMap: new Map(),
    }));

    expect(result.current.map(s => s.songId)).toEqual(['s2', 's3', 's1']);
  });

  it('sorts by intensity mode descending using the selected instrument difficulty', () => {
    const songsWithIntensity = [
      { ...songs[0], difficulty: { guitar: 5 } },
      { ...songs[1], difficulty: { guitar: 1 } },
      { ...songs[2], difficulty: { guitar: 3 } },
    ] as Song[];

    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithIntensity,
      search: '',
      sortMode: 'intensity' as any,
      sortAscending: false,
      filters: emptyFilters as any,
      instrument: 'Solo_Guitar' as InstrumentKey,
      scoreMap: new Map(),
      allScoreMap: new Map(),
    }));

    expect(result.current.map(s => s.songId)).toEqual(['s1', 's3', 's2']);
  });

  it.each([
    ['Solo_PeripheralVocals', { proVocals: 5 }, { proVocals: 1 }, { proVocals: 3 }],
    ['Solo_PeripheralDrums', { proDrums: 5 }, { proDrums: 1 }, { proDrums: 3 }],
    ['Solo_PeripheralCymbals', { proCymbals: 5 }, { proCymbals: 1 }, { proCymbals: 3 }],
  ] as const)('sorts by intensity mode ascending for %s using the normalized song difficulty', (instrument, firstDifficulty, secondDifficulty, thirdDifficulty) => {
    const songsWithIntensity = [
      { ...songs[0], difficulty: firstDifficulty },
      { ...songs[1], difficulty: secondDifficulty },
      { ...songs[2], difficulty: thirdDifficulty },
    ] as Song[];

    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithIntensity,
      search: '',
      sortMode: 'intensity' as any,
      sortAscending: true,
      filters: emptyFilters as any,
      instrument: instrument as InstrumentKey,
      scoreMap: new Map(),
      allScoreMap: new Map(),
    }));

    expect(result.current.map(s => s.songId)).toEqual(['s2', 's3', 's1']);
  });

  it('hides songs without Mic Mode when Mic Mode is selected', () => {
    const songsWithMicMode = [
      { ...songs[0], difficulty: { proVocals: 4 } },
      { ...songs[1], difficulty: { proVocals: null } },
      { ...songs[2], difficulty: { proVocals: 2 } },
    ] as Song[];

    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithMicMode,
      search: '',
      sortMode: 'title' as any,
      sortAscending: true,
      filters: emptyFilters as any,
      instrument: 'Solo_PeripheralVocals' as InstrumentKey,
      scoreMap: new Map(),
      allScoreMap: new Map(),
    }));

    expect(result.current.map(s => s.songId)).toEqual(['s1', 's3']);
  });
});

describe('useFilteredSongs — additional filter branches', () => {
  it('percentile bracket 100: rank equals totalEntries', () => {
    const scoreMap = new Map([['s1', score('s1', { rank: 100, totalEntries: 100 })]]);
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1')]])]]);
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: { ...emptyFilters, percentileFilter: { 100: true } } as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    expect(result.current.some(s => s.songId === 's1')).toBe(true);
  });

  it('missingScores filter with allScoreMap entry but empty instrument map', () => {
    const allScoreMap = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map()],
    ]);
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: { ...emptyFilters, missingScores: { guitar: true } } as any, instrument: null,
      scoreMap: new Map(), allScoreMap,
    }));
    expect(result.current.length).toBeGreaterThan(0);
  });

  it('score with undefined totalEntries in percentile calc', () => {
    const scoreMap = new Map([['s1', score('s1', { rank: 5, totalEntries: undefined as any })]]);
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1')]])]]);
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: { ...emptyFilters, percentileFilter: { 0: false } } as any, instrument: 'guitar' as InstrumentKey,
      scoreMap, allScoreMap,
    }));
    expect(result.current.length).toBeLessThanOrEqual(3);
  });

  it('instrument-specific filter with non-matching instrument is no-op', () => {
    const allScoreMap = new Map([['s1', new Map([['guitar' as InstrumentKey, score('s1')]])]]);
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: { ...emptyFilters, hasScores: { bass: true } } as any, instrument: 'guitar' as InstrumentKey,
      scoreMap: new Map(), allScoreMap,
    }));
    expect(result.current.length).toBe(3);
  });

  it('combined hasScores + hasFCs both must pass', () => {
    const allScoreMap = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['guitar' as InstrumentKey, score('s1', { isFullCombo: true })]])],
      ['s2', new Map([['guitar' as InstrumentKey, score('s2', { isFullCombo: false })]])],
    ]);
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: { ...emptyFilters, hasScores: { guitar: true }, hasFCs: { guitar: true } } as any, instrument: null,
      scoreMap: new Map(), allScoreMap,
    }));
    expect(result.current.length).toBe(1);
    expect(result.current[0]!.songId).toBe('s1');
  });

  it('sort by hasfc with empty scoreMap returns all songs', () => {
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'hasfc' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: null,
      scoreMap: new Map(), allScoreMap: new Map(),
    }));
    expect(result.current.length).toBe(3);
  });
});

describe('useFilteredSongs — instrument-specific filters ignored when no instrument selected', () => {
  it('ignores starsFilter when instrument is null', () => {
    const scoreMap = new Map([['s1', score('s1', { stars: 5 })], ['s2', score('s2', { stars: 3 })], ['s3', score('s3', { stars: 5 })]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, starsFilter: { 5: true, 3: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    // All songs returned because starsFilter is ignored without an instrument
    expect(result.current).toHaveLength(3);
  });

  it('ignores seasonFilter when instrument is null', () => {
    const scoreMap = new Map([['s1', score('s1', { season: 1 })], ['s2', score('s2', { season: 2 })], ['s3', score('s3', { season: 1 })]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, seasonFilter: { 1: true, 2: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    expect(result.current).toHaveLength(3);
  });

  it('ignores percentileFilter when instrument is null', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { rank: 1, totalEntries: 100 })],
      ['s2', score('s2', { rank: 50, totalEntries: 100 })],
      ['s3', score('s3', { rank: 90, totalEntries: 100 })],
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, percentileFilter: { 1: true, 50: false, 90: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    expect(result.current).toHaveLength(3);
  });

  it('ignores difficultyFilter when instrument is null', () => {
    const songsWithDiff = [
      { ...songs[0], difficulty: { guitar: 3 } },
      { ...songs[1], difficulty: { guitar: 5 } },
      { ...songs[2], difficulty: { guitar: 3 } },
    ];
    const scoreMap = new Map([['s1', score('s1')], ['s2', score('s2')], ['s3', score('s3')]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, difficultyFilter: { 4: true, 6: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithDiff as any, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    expect(result.current).toHaveLength(3);
  });
});

describe('useFilteredSongs — maxdistance sort', () => {
  const inst = 'Solo_Guitar' as InstrumentKey;

  const songsWithMax = [
    song('s1', 'Alpha', 'A', 2020, { Solo_Guitar: 100000 }),
    song('s2', 'Beta', 'B', 2021, { Solo_Guitar: 100000 }),
    song('s3', 'Gamma', 'C', 2022, { Solo_Guitar: 100000 }),
  ];

  it('sorts by score / maxScore ratio descending', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { score: 80000 })],  // 80%
      ['s2', score('s2', { score: 95000 })],  // 95%
      ['s3', score('s3', { score: 70000 })],  // 70%
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([[inst, s]])]));
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithMax, search: '', sortMode: 'maxdistance' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: inst,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.title)).toEqual(['Beta', 'Alpha', 'Gamma']);
  });

  it('sorts by score / maxScore ratio ascending', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { score: 80000 })],
      ['s2', score('s2', { score: 95000 })],
      ['s3', score('s3', { score: 70000 })],
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([[inst, s]])]));
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithMax, search: '', sortMode: 'maxdistance' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: inst,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.title)).toEqual(['Gamma', 'Alpha', 'Beta']);
  });

  it('songs without player score sort last (descending)', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { score: 80000 })],
      // s2 has no score
      ['s3', score('s3', { score: 70000 })],
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([[inst, s]])]));
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithMax, search: '', sortMode: 'maxdistance' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: inst,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.title)).toEqual(['Alpha', 'Gamma', 'Beta']);
  });

  it('songs without max score sort last (descending)', () => {
    const songsPartialMax = [
      song('s1', 'Alpha', 'A', 2020, { Solo_Guitar: 100000 }),
      song('s2', 'Beta', 'B', 2021),  // no maxScores
      song('s3', 'Gamma', 'C', 2022, { Solo_Guitar: 100000 }),
    ];
    const scoreMap = new Map([
      ['s1', score('s1', { score: 80000 })],
      ['s2', score('s2', { score: 95000 })],
      ['s3', score('s3', { score: 70000 })],
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([[inst, s]])]));
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsPartialMax, search: '', sortMode: 'maxdistance' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: inst,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.title)).toEqual(['Alpha', 'Gamma', 'Beta']);
  });

  it('falls back to score sort when no songs have maxScores (descending)', () => {
    const songsNoMax = [
      song('s1', 'Alpha', 'A', 2020),
      song('s2', 'Beta', 'B', 2021),
      song('s3', 'Gamma', 'C', 2022),
    ];
    const scoreMap = new Map([
      ['s1', score('s1', { score: 80000 })],
      ['s2', score('s2', { score: 95000 })],
      ['s3', score('s3', { score: 70000 })],
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([[inst, s]])]));
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsNoMax, search: '', sortMode: 'maxdistance' as any, sortAscending: false,
      filters: emptyFilters as any, instrument: inst,
      scoreMap, allScoreMap,
    }));
    // Falls back to raw score comparison: 95k > 80k > 70k
    expect(result.current.map(s => s.title)).toEqual(['Beta', 'Alpha', 'Gamma']);
  });

  it('songs without player score sort last (ascending)', () => {
    const scoreMap = new Map([
      ['s1', score('s1', { score: 80000 })],
      // s2 has no score
      ['s3', score('s3', { score: 70000 })],
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([[inst, s]])]));
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithMax, search: '', sortMode: 'maxdistance' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: inst,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.title)).toEqual(['Gamma', 'Alpha', 'Beta']);
  });

  it('songs without max score sort last (ascending)', () => {
    const songsPartialMax = [
      song('s1', 'Alpha', 'A', 2020, { Solo_Guitar: 100000 }),
      song('s2', 'Beta', 'B', 2021),  // no maxScores
      song('s3', 'Gamma', 'C', 2022, { Solo_Guitar: 100000 }),
    ];
    const scoreMap = new Map([
      ['s1', score('s1', { score: 80000 })],
      ['s2', score('s2', { score: 95000 })],
      ['s3', score('s3', { score: 70000 })],
    ]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([[inst, s]])]));
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsPartialMax, search: '', sortMode: 'maxdistance' as any, sortAscending: true,
      filters: emptyFilters as any, instrument: inst,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.title)).toEqual(['Gamma', 'Alpha', 'Beta']);
  });
});
