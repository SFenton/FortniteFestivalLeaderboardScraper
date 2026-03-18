import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { useFilteredSongs } from '../../hooks/data/useFilteredSongs';
import type { ServerSong as Song, PlayerScore, ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';

function song(id: string, title: string, artist: string, year?: number): Song {
  return { songId: id, title, artist, year: year ?? 2024, albumArt: '', maxScores: null } as unknown as Song;
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

  it('applies season filter', () => {
    const scoreMap = new Map([['s1', score('s1', { season: 1 })], ['s2', score('s2', { season: 2 })], ['s3', score('s3', { season: 1 })]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, seasonFilter: { 1: true, 2: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
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
      filters: filters as any, instrument: null,
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
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    expect(result.current.map(s => s.songId)).toEqual(['s1']);
  });

  it('applies difficulty filter', () => {
    const songsWithDiff = [
      { ...songs[0], difficulty: 3 },
      { ...songs[1], difficulty: 5 },
      { ...songs[2], difficulty: 3 },
    ];
    const scoreMap = new Map([['s1', score('s1')], ['s2', score('s2')], ['s3', score('s3')]]);
    const allScoreMap = new Map(Array.from(scoreMap.entries()).map(([id, s]) => [id, new Map([['guitar' as InstrumentKey, s]])]));
    const filters = { ...emptyFilters, difficultyFilter: { 3: true, 5: false } };
    const { result } = renderHook(() => useFilteredSongs({
      songs: songsWithDiff as any, search: '', sortMode: 'title' as any, sortAscending: true,
      filters: filters as any, instrument: null,
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
      filters: filters as any, instrument: null,
      scoreMap, allScoreMap,
    }));
    // s1 has rank 0 → bracket 0 → filtered out; s2 and s3 have no score → bracket 0 → filtered out
    expect(result.current).toHaveLength(0);
  });
});
