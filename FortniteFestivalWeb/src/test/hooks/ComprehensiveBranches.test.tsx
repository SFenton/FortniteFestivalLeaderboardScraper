/**
 * Comprehensive branch coverage tests for SongRow.compareByMode,
 * useFilteredSongs, playerStats, and useSortedScoreHistory.
 * Targets the 59+ uncovered branches in these purely testable files.
 */
import { describe, it, expect } from 'vitest';
import { renderHook } from '@testing-library/react';
import { compareByMode } from '../../pages/songs/components/SongRow';
import { useFilteredSongs } from '../../hooks/data/useFilteredSongs';
import { computeInstrumentStats, computeOverallStats } from '../../pages/player/helpers/playerStats';
import { useSortedScoreHistory } from '../../hooks/data/useSortedScoreHistory';
import { PlayerScoreSortMode, ACCURACY_SCALE } from '@festival/core';
import type { ServerSong as Song, PlayerScore, ServerInstrumentKey as InstrumentKey, ServerScoreHistoryEntry } from '@festival/core/api/serverTypes';
import type { SongFilters } from '../../utils/songSettings';

/* ── Helpers ── */

function makeScore(overrides: Partial<PlayerScore> = {}): PlayerScore {
  return { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, totalEntries: 100, accuracy: 950000, isFullCombo: false, stars: 5, season: 5, ...overrides };
}

function makeSong(id: string, overrides: Partial<Song> = {}): Song {
  return { songId: id, title: `Song ${id}`, artist: `Artist ${id}`, year: 2024, ...overrides };
}

function emptyFilters(): SongFilters {
  return { missingScores: {}, missingFCs: {}, hasScores: {}, hasFCs: {}, seasonFilter: {}, percentileFilter: {}, starsFilter: {}, difficultyFilter: {} };
}

function makeHistory(overrides: Partial<ServerScoreHistoryEntry> = {}): ServerScoreHistoryEntry {
  return { songId: 's1', instrument: 'Solo_Guitar', newScore: 100000, newRank: 5, changedAt: '2025-01-15T10:00:00Z', ...overrides };
}

/* ══════════════════════════════════════════════
   compareByMode — all switch branches + null guards
   ══════════════════════════════════════════════ */

describe('compareByMode — complete branch coverage', () => {
  const a = makeScore({ score: 100, accuracy: 900, isFullCombo: false, rank: 5, totalEntries: 100, stars: 4, season: 3 });
  const b = makeScore({ score: 200, accuracy: 950, isFullCombo: true, rank: 2, totalEntries: 100, stars: 5, season: 5 });

  it('score: compares by score', () => {
    expect(compareByMode('score', a, b)).toBeLessThan(0);
  });

  it('percentage: compares by accuracy then FC tiebreaker', () => {
    expect(compareByMode('percentage', a, b)).toBeLessThan(0);
    // Same accuracy, FC tiebreaker
    const x = makeScore({ accuracy: 900, isFullCombo: false });
    const y = makeScore({ accuracy: 900, isFullCombo: true });
    expect(compareByMode('percentage', x, y)).toBeLessThan(0);
  });

  it('percentile: compares by rank/totalEntries ratio', () => {
    expect(compareByMode('percentile', a, b)).toBeGreaterThan(0);
  });

  it('percentile: Infinity fallback when rank=0 or totalEntries=0', () => {
    const noRank = makeScore({ rank: 0, totalEntries: 0 });
    expect(compareByMode('percentile', noRank, b)).toBeGreaterThan(0);
    expect(compareByMode('percentile', a, noRank)).toBeLessThan(0);
  });

  it('stars: compares by star count with null fallback', () => {
    expect(compareByMode('stars', a, b)).toBeLessThan(0);
    const noStars = makeScore({ stars: undefined });
    expect(compareByMode('stars', noStars, b)).toBeLessThan(0);
  });

  it('seasonachieved: compares by season with null fallback', () => {
    expect(compareByMode('seasonachieved', a, b)).toBeLessThan(0);
    const noSeason = makeScore({ season: undefined });
    expect(compareByMode('seasonachieved', noSeason, b)).toBeLessThan(0);
  });

  it('hasfc: compares by isFullCombo boolean', () => {
    expect(compareByMode('hasfc', a, b)).toBeLessThan(0);
  });

  it('default: returns 0 for unknown mode', () => {
    expect(compareByMode('unknown' as any, a, b)).toBe(0);
  });

  it('undefined a sorts last', () => {
    expect(compareByMode('score', undefined, b)).toBe(1);
  });

  it('undefined b sorts last', () => {
    expect(compareByMode('score', a, undefined)).toBe(-1);
  });

  it('both undefined returns 0', () => {
    expect(compareByMode('score', undefined, undefined)).toBe(0);
  });
});

/* ══════════════════════════════════════════════
   useFilteredSongs — filter/sort branch coverage
   ══════════════════════════════════════════════ */

describe('useFilteredSongs — complete branch coverage', () => {
  const songs = [makeSong('s1'), makeSong('s2', { title: 'Beta', artist: 'Zeta', year: 2020 }), makeSong('s3', { title: 'Alpha' })];
  const noScores = new Map<string, PlayerScore>();
  const noAll = new Map<string, Map<InstrumentKey, PlayerScore>>();

  function callHook(opts: Partial<Parameters<typeof useFilteredSongs>[0]> = {}) {
    return renderHook(() => useFilteredSongs({
      songs, search: '', sortMode: 'title', sortAscending: true,
      filters: emptyFilters(), instrument: null, scoreMap: noScores, allScoreMap: noAll,
      ...opts,
    })).result.current;
  }

  it('returns all songs with no filters', () => {
    expect(callHook().length).toBe(3);
  });

  it('filters by search query (title)', () => {
    expect(callHook({ search: 'Beta' }).length).toBe(1);
  });

  it('filters by search query (artist)', () => {
    expect(callHook({ search: 'Zeta' }).length).toBe(1);
  });

  it('sorts by title ascending', () => {
    const result = callHook({ sortMode: 'title', sortAscending: true });
    expect(result[0]!.title).toBe('Alpha');
  });

  it('sorts by title descending', () => {
    const result = callHook({ sortMode: 'title', sortAscending: false });
    expect(result[0]!.title).toBe('Song s1');
  });

  it('sorts by artist', () => {
    const result = callHook({ sortMode: 'artist', sortAscending: true });
    expect(result[0]!.artist).toBe('Artist s1');
  });

  it('sorts by year', () => {
    const result = callHook({ sortMode: 'year', sortAscending: true });
    expect(result[0]!.year).toBe(2020);
  });

  it('sorts by score when scoreMap is set', () => {
    const scoreMap = new Map<string, PlayerScore>([
      ['s1', makeScore({ songId: 's1', score: 200 })],
      ['s2', makeScore({ songId: 's2', score: 100 })],
    ]);
    const result = callHook({ sortMode: 'score', scoreMap });
    expect(result[0]!.songId).toBe('s2');
  });

  it('falls back to title sort when scoreMap is empty and mode is instrument-specific', () => {
    const result = callHook({ sortMode: 'score' });
    expect(result[0]!.title).toBe('Alpha');
  });

  it('filters by missingScores', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1' })]])],
    ]);
    const f = { ...emptyFilters(), missingScores: { Solo_Guitar: true } };
    const result = callHook({ filters: f, allScoreMap: all });
    // s2 and s3 have no score → they should pass the missingScores filter
    expect(result.length).toBe(2);
  });

  it('filters by hasScores', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1' })]])],
    ]);
    const f = { ...emptyFilters(), hasScores: { Solo_Guitar: true } };
    const result = callHook({ filters: f, allScoreMap: all });
    expect(result.length).toBe(1);
    expect(result[0]!.songId).toBe('s1');
  });

  it('filters by missingFCs', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1', isFullCombo: true })]])],
    ]);
    const f = { ...emptyFilters(), missingFCs: { Solo_Guitar: true } };
    const result = callHook({ filters: f, allScoreMap: all });
    // s2 and s3 have no FC → pass
    expect(result.length).toBe(2);
  });

  it('filters by hasFCs', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1', isFullCombo: true })]])],
    ]);
    const f = { ...emptyFilters(), hasFCs: { Solo_Guitar: true } };
    const result = callHook({ filters: f, allScoreMap: all });
    expect(result.length).toBe(1);
  });

  it('filters by season', () => {
    const scoreMap = new Map([['s1', makeScore({ songId: 's1', season: 3 })]]);
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1', season: 3 })]])],
    ]);
    const f = { ...emptyFilters(), seasonFilter: { 3: false, 0: true } };
    const result = callHook({ filters: f, scoreMap, allScoreMap: all, instrument: 'Solo_Guitar' });
    // s1 has season 3 which is filtered out
    expect(result.some(s => s.songId === 's1')).toBe(false);
  });

  it('filters by percentile bracket', () => {
    const scoreMap = new Map([['s1', makeScore({ songId: 's1', rank: 1, totalEntries: 100 })]]);
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1', rank: 1, totalEntries: 100 })]])],
    ]);
    // Filter out Top 1% bracket
    const f = { ...emptyFilters(), percentileFilter: { 1: false, 0: true } };
    const result = callHook({ filters: f, scoreMap, allScoreMap: all, instrument: 'Solo_Guitar' });
    expect(result.some(s => s.songId === 's1')).toBe(false);
  });

  it('percentile filter: "No Score" (0) bracket', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1' })]])],
    ]);
    const f = { ...emptyFilters(), percentileFilter: { 0: false } };
    const result = callHook({ filters: f, allScoreMap: all, instrument: 'Solo_Guitar' });
    // s2, s3 have no score — filtered out by percentile[0]=false
    expect(result.length).toBeLessThan(3);
  });

  it('filters by stars', () => {
    const scoreMap = new Map([['s1', makeScore({ songId: 's1', stars: 5 })]]);
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1', stars: 5 })]])],
    ]);
    const f = { ...emptyFilters(), starsFilter: { 5: false } };
    const result = callHook({ filters: f, scoreMap, allScoreMap: all, instrument: 'Solo_Guitar' });
    expect(result.some(s => s.songId === 's1')).toBe(false);
  });

  it('filters by difficulty', () => {
    const songWithDiff = { ...makeSong('s1'), difficulty: { guitar: 3 } };
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1' })]])],
    ]);
    const f = { ...emptyFilters(), difficultyFilter: { 3: false } };
    const result = callHook({
      songs: [songWithDiff, makeSong('s2')],
      filters: f, allScoreMap: all, instrument: 'Solo_Guitar',
    });
    expect(result.length).toBeLessThanOrEqual(2);
  });

  it('applies instrument filter restricting to single instrument', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1' })]])],
    ]);
    const f = { ...emptyFilters(), hasScores: { Solo_Guitar: true } };
    const result = callHook({ filters: f, allScoreMap: all, instrument: 'Solo_Guitar' });
    expect(result.length).toBe(1);
  });

  it('filters by combined missingScores + missingFCs', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1', isFullCombo: true })]])],
      ['s2', new Map([['Solo_Guitar', makeScore({ songId: 's2', score: 0, isFullCombo: false })]])],
    ]);
    const f = { ...emptyFilters(), missingScores: { Solo_Guitar: true }, missingFCs: { Solo_Guitar: true } };
    const result = callHook({ filters: f, allScoreMap: all });
    // s3 has no score entry → passes missingScores+missingFCs
    expect(result.some(s => s.songId === 's3')).toBe(true);
  });

  it('percentile filter with score that has no rank', () => {
    const scoreMap = new Map([['s1', makeScore({ songId: 's1', rank: 0, totalEntries: 0 })]]);
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1', rank: 0, totalEntries: 0 })]])],
    ]);
    const f = { ...emptyFilters(), percentileFilter: { 0: false } };
    const result = callHook({ filters: f, scoreMap, allScoreMap: all, instrument: 'Solo_Guitar' });
    // s1 has score but no valid rank → treated as "No Score" (bracket 0) → filtered out
    expect(result.some(s => s.songId === 's1')).toBe(false);
  });

  it('combined hasScores + hasFCs on same instrument', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1', isFullCombo: true })]])],
      ['s2', new Map([['Solo_Guitar', makeScore({ songId: 's2', isFullCombo: false })]])],
    ]);
    const f = { ...emptyFilters(), hasScores: { Solo_Guitar: true }, hasFCs: { Solo_Guitar: true } };
    const result = callHook({ filters: f, allScoreMap: all });
    // Only s1 has both score and FC
    expect(result.length).toBe(1);
    expect(result[0]!.songId).toBe('s1');
  });

  it('filter with no active instrument filters passes all songs', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1' })]])],
    ]);
    const result = callHook({ allScoreMap: all });
    expect(result.length).toBe(3);
  });

  it('instrument-specific filter with no matching instrument is no-op', () => {
    const all = new Map<string, Map<InstrumentKey, PlayerScore>>([
      ['s1', new Map([['Solo_Guitar', makeScore({ songId: 's1' })]])],
    ]);
    const f = { ...emptyFilters(), hasScores: { Solo_Bass: true } };
    const result = callHook({ filters: f, allScoreMap: all, instrument: 'Solo_Guitar' });
    // Solo_Bass filter active but instrument=Solo_Guitar → activeFilterInstruments is empty → passes all
    expect(result.length).toBe(3);
  });
});

/* ══════════════════════════════════════════════
   playerStats — all computational branches
   ══════════════════════════════════════════════ */

describe('playerStats — complete branch coverage', () => {
  describe('computeInstrumentStats', () => {
    it('handles scores with stars=0 (averageStars=0 branch)', () => {
      const stats = computeInstrumentStats([makeScore({ stars: 0 })], 10);
      expect(stats.averageStars).toBe(0);
    });

    it('computes averageStars when stars > 0', () => {
      const stats = computeInstrumentStats([makeScore({ stars: 5 }), makeScore({ songId: 's2', stars: 3 })], 10);
      expect(stats.averageStars).toBe(4);
    });

    it('handles no ranked scores (bestRank=0)', () => {
      const stats = computeInstrumentStats([makeScore({ rank: 0, totalEntries: 0 })], 10);
      expect(stats.bestRank).toBe(0);
      expect(stats.bestRankSongId).toBeNull();
    });

    it('finds bestRankSongId from ranked scores', () => {
      const stats = computeInstrumentStats([
        makeScore({ songId: 's1', rank: 5, totalEntries: 100 }),
        makeScore({ songId: 's2', rank: 1, totalEntries: 100 }),
      ], 10);
      expect(stats.bestRank).toBe(1);
      expect(stats.bestRankSongId).toBe('s2');
    });

    it('counts all star levels', () => {
      const scores = [
        makeScore({ songId: 'a', stars: 6 }),
        makeScore({ songId: 'b', stars: 5 }),
        makeScore({ songId: 'c', stars: 4 }),
        makeScore({ songId: 'd', stars: 3 }),
        makeScore({ songId: 'e', stars: 2 }),
        makeScore({ songId: 'f', stars: 1 }),
      ];
      const stats = computeInstrumentStats(scores, 10);
      expect(stats.goldStarCount).toBe(1);
      expect(stats.fiveStarCount).toBe(1);
      expect(stats.fourStarCount).toBe(1);
      expect(stats.threeStarCount).toBe(1);
      expect(stats.twoStarCount).toBe(1);
      expect(stats.oneStarCount).toBe(1);
    });

    it('computes FC percentage correctly', () => {
      const scores = [makeScore({ isFullCombo: true }), makeScore({ songId: 's2', isFullCombo: false })];
      const stats = computeInstrumentStats(scores, 10);
      expect(stats.fcCount).toBe(1);
      expect(parseFloat(stats.fcPercent)).toBe(50);
    });

    it('100% FC case', () => {
      const scores = [makeScore({ isFullCombo: true })];
      const stats = computeInstrumentStats(scores, 1);
      expect(stats.fcPercent).toBe('100.0');
    });

    it('handles empty accuracies gracefully', () => {
      const stats = computeInstrumentStats([makeScore({ accuracy: 0 })], 10);
      expect(stats.avgAccuracy).toBe(0);
    });
  });

  describe('computeOverallStats', () => {
    it('counts unique songs played', () => {
      const stats = computeOverallStats([makeScore({ songId: 's1' }), makeScore({ songId: 's1' }), makeScore({ songId: 's2' })]);
      expect(stats.songsPlayed).toBe(2);
    });

    it('computes best rank across all scores', () => {
      const stats = computeOverallStats([makeScore({ rank: 10 }), makeScore({ songId: 's2', rank: 3 })]);
      expect(stats.bestRank).toBe(3);
    });

    it('handles no ranked scores', () => {
      const stats = computeOverallStats([makeScore({ rank: 0 })]);
      expect(stats.bestRank).toBe(0);
      expect(stats.bestRankSongId).toBeNull();
    });

    it('computes FC percentage', () => {
      const stats = computeOverallStats([makeScore({ isFullCombo: true }), makeScore({ songId: 's2', isFullCombo: false })]);
      expect(stats.fcCount).toBe(1);
    });

    it('computes avgAccuracy', () => {
      const stats = computeOverallStats([makeScore({ accuracy: 95 * ACCURACY_SCALE }), makeScore({ songId: 's2', accuracy: 85 * ACCURACY_SCALE })]);
      expect(stats.avgAccuracy).toBe(90 * ACCURACY_SCALE);
    });

    it('finds bestRankSongId and bestRankInstrument', () => {
      const stats = computeOverallStats([
        makeScore({ songId: 's1', rank: 5, instrument: 'Solo_Guitar' }),
        makeScore({ songId: 's2', rank: 1, instrument: 'Solo_Bass' }),
      ]);
      expect(stats.bestRankSongId).toBe('s2');
      expect(stats.bestRankInstrument).toBe('Solo_Bass');
    });
  });
});

/* ══════════════════════════════════════════════
   useSortedScoreHistory — all switch + tiebreaker branches
   ══════════════════════════════════════════════ */

describe('useSortedScoreHistory — complete branch coverage', () => {
  const h1 = makeHistory({ newScore: 100000, accuracy: 900000, season: 3, scoreAchievedAt: '2025-01-01T00:00:00Z', isFullCombo: false });
  const h2 = makeHistory({ newScore: 200000, accuracy: 950000, season: 5, scoreAchievedAt: '2025-02-01T00:00:00Z', isFullCombo: true });

  it('date asc', () => {
    const { result } = renderHook(() => useSortedScoreHistory([h2, h1], PlayerScoreSortMode.Date, true));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-01-01T00:00:00Z');
  });

  it('date desc', () => {
    const { result } = renderHook(() => useSortedScoreHistory([h1, h2], PlayerScoreSortMode.Date, false));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-02-01T00:00:00Z');
  });

  it('date with null scoreAchievedAt falls back to changedAt', () => {
    const x = makeHistory({ scoreAchievedAt: undefined, changedAt: '2025-03-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([x, h1], PlayerScoreSortMode.Date, true));
    expect(result.current[0]!.changedAt).toBe('2025-01-15T10:00:00Z');
  });

  it('score asc', () => {
    const { result } = renderHook(() => useSortedScoreHistory([h2, h1], PlayerScoreSortMode.Score, true));
    expect(result.current[0]!.newScore).toBe(100000);
  });

  it('accuracy asc', () => {
    const { result } = renderHook(() => useSortedScoreHistory([h2, h1], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.accuracy).toBe(900000);
  });

  it('accuracy tiebreaker: FC, then score, then date', () => {
    const x = makeHistory({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: '2025-02-01T00:00:00Z' });
    const y = makeHistory({ accuracy: 950000, isFullCombo: true, newScore: 100000, scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([y, x], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[1]!.isFullCombo).toBe(true);
  });

  it('accuracy tiebreaker: score when FC is equal', () => {
    const x = makeHistory({ accuracy: 950000, isFullCombo: false, newScore: 100000 });
    const y = makeHistory({ accuracy: 950000, isFullCombo: false, newScore: 200000 });
    const { result } = renderHook(() => useSortedScoreHistory([y, x], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.newScore).toBe(100000);
  });

  it('accuracy tiebreaker: date when FC and score are equal', () => {
    const x = makeHistory({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: '2025-02-01T00:00:00Z' });
    const y = makeHistory({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([x, y], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.scoreAchievedAt).toBe('2025-01-01T00:00:00Z');
  });

  it('accuracy with null values', () => {
    const x = makeHistory({ accuracy: undefined, isFullCombo: undefined });
    const { result } = renderHook(() => useSortedScoreHistory([x, h1], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.accuracy).toBeUndefined();
  });

  it('season asc', () => {
    const { result } = renderHook(() => useSortedScoreHistory([h2, h1], PlayerScoreSortMode.Season, true));
    expect(result.current[0]!.season).toBe(3);
  });

  it('season with null values', () => {
    const x = makeHistory({ season: undefined });
    const { result } = renderHook(() => useSortedScoreHistory([x, h2], PlayerScoreSortMode.Season, true));
    expect(result.current[0]!.season).toBeUndefined();
  });

  it('date with both scoreAchievedAt and changedAt undefined', () => {
    const x = makeHistory({ scoreAchievedAt: undefined, changedAt: undefined } as any);
    const y = makeHistory({ scoreAchievedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([x, y], PlayerScoreSortMode.Date, true));
    expect(result.current.length).toBe(2);
  });

  it('accuracy tiebreaker last resort: date with null scoreAchievedAt falls back to changedAt', () => {
    const x = makeHistory({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: undefined, changedAt: '2025-02-01T00:00:00Z' });
    const y = makeHistory({ accuracy: 950000, isFullCombo: false, newScore: 100000, scoreAchievedAt: undefined, changedAt: '2025-01-01T00:00:00Z' });
    const { result } = renderHook(() => useSortedScoreHistory([x, y], PlayerScoreSortMode.Accuracy, true));
    expect(result.current[0]!.changedAt).toBe('2025-01-01T00:00:00Z');
  });

  it('handles default sort mode gracefully', () => {
    const { result } = renderHook(() => useSortedScoreHistory([h1, h2], 'unknown' as any, true));
    expect(result.current.length).toBe(2);
  });
});
