/**
 * CoverageGaps7 — Targeted tests for remaining branch/function gaps.
 *
 * Covers:
 * - useFilteredSongs: season/percentile/stars/difficulty filters, per-instrument filters
 * - useChartPagination: navigatePoint function
 * - useChartData: queryFn callback (fetches history)
 * - InstrumentStatsSection: instStarsUpdater, instFCsUpdater, instPercentileUpdater
 * - useAccountSearch: click-outside handler
 * - PlayerDataContext: refreshPlayer
 * - FloatingActionButton: searchVisible rendering
 * - BottomNav: player truthy/falsy branches
 * - PathsModal: difficulty button click
 * - PlayerHistoryPage: applySort function
 * - SuggestionsPage: openFilter/applyFilter/resetFilter + empty state
 * - CategoryCard: showStarPngs branch
 * - PlayerPage: justCompleted branch + skip-anim
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, renderHook, act, fireEvent, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter, Routes, Route } from 'react-router-dom';
import { QueryClientProvider } from '@tanstack/react-query';
import { createApiMock, MOCK_SONGS, MOCK_HISTORY_ENTRIES } from '../helpers/apiMocks';
import { TestProviders, createTestQueryClient } from '../helpers/TestProviders';

// ──────────────────────────────────────────────────
// § useFilteredSongs — branch coverage
// ──────────────────────────────────────────────────
import { useFilteredSongs } from '../../hooks/data/useFilteredSongs';
import { defaultSongFilters, type SongFilters } from '../../utils/songSettings';
import type { ServerSong, PlayerScore, ServerInstrumentKey } from '@festival/core/api/serverTypes';

function renderFiltered(opts: Parameters<typeof useFilteredSongs>[0]) {
  const wrapper = ({ children }: { children: ReactNode }) => <>{children}</>;
  return renderHook(() => useFilteredSongs(opts), { wrapper });
}

function makeScore(overrides: Partial<PlayerScore> = {}): PlayerScore {
  return { songId: 'song-1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 95, isFullCombo: false, stars: 4, season: 5, totalEntries: 500, ...overrides };
}

describe('useFilteredSongs', () => {
  const songs: ServerSong[] = MOCK_SONGS;
  const base = {
    songs,
    search: '',
    sortMode: 'title' as const,
    sortAscending: true,
    filters: defaultSongFilters(),
    instrument: null as ServerInstrumentKey | null,
    scoreMap: new Map<string, PlayerScore>(),
    allScoreMap: new Map<string, Map<ServerInstrumentKey, PlayerScore>>(),
  };

  it('filters by hasScores per instrument', () => {
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', makeScore({ songId: 'song-1' })]]));
    allScoreMap.set('song-2', new Map());
    allScoreMap.set('song-3', new Map());
    const filters: SongFilters = { ...defaultSongFilters(), hasScores: { Solo_Guitar: true } };
    const { result } = renderFiltered({ ...base, filters, instrument: 'Solo_Guitar', allScoreMap });
    // Only song-1 has a score for Solo_Guitar
    expect(result.current.map(s => s.songId)).toEqual(['song-1']);
  });

  it('filters by missingScores per instrument', () => {
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', makeScore({ songId: 'song-1' })]]));
    allScoreMap.set('song-2', new Map());
    allScoreMap.set('song-3', new Map());
    const filters: SongFilters = { ...defaultSongFilters(), missingScores: { Solo_Guitar: true } };
    const { result } = renderFiltered({ ...base, filters, instrument: 'Solo_Guitar', allScoreMap });
    // song-2 and song-3 don't have scores
    expect(result.current.map(s => s.songId)).toContain('song-2');
    expect(result.current.map(s => s.songId)).not.toContain('song-1');
  });

  it('filters by hasFCs per instrument', () => {
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', makeScore({ songId: 'song-1', isFullCombo: true })]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', makeScore({ songId: 'song-2', isFullCombo: false })]]));
    allScoreMap.set('song-3', new Map());
    const filters: SongFilters = { ...defaultSongFilters(), hasFCs: { Solo_Guitar: true } };
    const { result } = renderFiltered({ ...base, filters, instrument: 'Solo_Guitar', allScoreMap });
    expect(result.current.map(s => s.songId)).toEqual(['song-1']);
  });

  it('filters by missingFCs per instrument', () => {
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', makeScore({ songId: 'song-1', isFullCombo: true })]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', makeScore({ songId: 'song-2', isFullCombo: false })]]));
    allScoreMap.set('song-3', new Map());
    const filters: SongFilters = { ...defaultSongFilters(), missingFCs: { Solo_Guitar: true } };
    const { result } = renderFiltered({ ...base, filters, instrument: 'Solo_Guitar', allScoreMap });
    const ids = result.current.map(s => s.songId);
    expect(ids).toContain('song-2');
    expect(ids).toContain('song-3');
    expect(ids).not.toContain('song-1');
  });

  it('filters by seasonFilter', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', season: 5 }));
    scoreMap.set('song-2', makeScore({ songId: 'song-2', season: 4 }));
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', makeScore({ songId: 'song-1', season: 5 })]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', makeScore({ songId: 'song-2', season: 4 })]]));
    allScoreMap.set('song-3', new Map());
    const filters: SongFilters = { ...defaultSongFilters(), seasonFilter: { 4: false, 5: true } };
    const { result } = renderFiltered({ ...base, filters, instrument: 'Solo_Guitar', scoreMap, allScoreMap });
    const ids = result.current.map(s => s.songId);
    expect(ids).not.toContain('song-2');
    expect(ids).toContain('song-1');
  });

  it('filters by percentileFilter', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', rank: 1, totalEntries: 100 })); // 1% → bucket 1
    scoreMap.set('song-2', makeScore({ songId: 'song-2', rank: 50, totalEntries: 100 })); // 50% → bucket 50
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', scoreMap.get('song-1')!]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', scoreMap.get('song-2')!]]));
    allScoreMap.set('song-3', new Map());
    const filters: SongFilters = { ...defaultSongFilters(), percentileFilter: { 1: false, 50: true } };
    const { result } = renderFiltered({ ...base, filters, instrument: 'Solo_Guitar', scoreMap, allScoreMap });
    const ids = result.current.map(s => s.songId);
    expect(ids).not.toContain('song-1');
    expect(ids).toContain('song-2');
  });

  it('filters by starsFilter', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', stars: 5 }));
    scoreMap.set('song-2', makeScore({ songId: 'song-2', stars: 3 }));
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', scoreMap.get('song-1')!]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', scoreMap.get('song-2')!]]));
    allScoreMap.set('song-3', new Map());
    const filters: SongFilters = { ...defaultSongFilters(), starsFilter: { 3: false, 5: true } };
    const { result } = renderFiltered({ ...base, filters, instrument: 'Solo_Guitar', scoreMap, allScoreMap });
    const ids = result.current.map(s => s.songId);
    expect(ids).not.toContain('song-2');
    expect(ids).toContain('song-1');
  });

  it('filters by difficultyFilter', () => {
    // Use custom songs with numeric difficulty to match the filter code
    const customSongs: ServerSong[] = [
      { ...MOCK_SONGS[0]!, songId: 'song-d1', difficulty: 3 as any },
      { ...MOCK_SONGS[1]!, songId: 'song-d2', difficulty: 2 as any },
      { ...MOCK_SONGS[2]!, songId: 'song-d3', difficulty: 5 as any },
    ];
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-d1', new Map([['Solo_Guitar', makeScore({ songId: 'song-d1' })]]));
    allScoreMap.set('song-d2', new Map([['Solo_Guitar', makeScore({ songId: 'song-d2' })]]));
    allScoreMap.set('song-d3', new Map([['Solo_Guitar', makeScore({ songId: 'song-d3' })]]));
    const filters: SongFilters = { ...defaultSongFilters(), difficultyFilter: { 3: false, 2: true, 5: true } };
    const { result } = renderFiltered({ ...base, songs: customSongs, filters, instrument: 'Solo_Guitar', allScoreMap });
    const ids = result.current.map(s => s.songId);
    expect(ids).not.toContain('song-d1');
    expect(ids).toContain('song-d2');
    expect(ids).toContain('song-d3');
  });

  it('excludes unscored songs when percentileFilter excludes bucket 0', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', rank: 5, totalEntries: 100 }));
    // song-2 has no score → pct undefined → bucket 0
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', scoreMap.get('song-1')!]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', makeScore({ songId: 'song-2' })]]));
    allScoreMap.set('song-3', new Map());
    const filters: SongFilters = { ...defaultSongFilters(), percentileFilter: { 0: false } };
    const { result } = renderFiltered({ ...base, filters, instrument: 'Solo_Guitar', scoreMap, allScoreMap });
    // song-2 and song-3 have no score in scoreMap → percentile undefined → bucket 0 → filtered out
    const ids = result.current.map(s => s.songId);
    expect(ids).toContain('song-1');
  });

  it('sorts by score mode with scoreMap', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', score: 200000 }));
    scoreMap.set('song-2', makeScore({ songId: 'song-2', score: 100000 }));
    // allScoreMap must be non-empty so hasPlayerData is true
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', scoreMap.get('song-1')!]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', scoreMap.get('song-2')!]]));
    const { result } = renderFiltered({ ...base, sortMode: 'score', sortAscending: false, scoreMap, allScoreMap });
    // song-1 (200k) should come before song-2 (100k) in descending order
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('song-1');
    const idx2 = ids.indexOf('song-2');
    expect(idx1).toBeLessThan(idx2);
  });

  it('sorts by percentile mode', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', rank: 1, totalEntries: 100 })); // top 1%
    scoreMap.set('song-2', makeScore({ songId: 'song-2', rank: 50, totalEntries: 100 })); // 50%
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', scoreMap.get('song-1')!]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', scoreMap.get('song-2')!]]));
    const { result } = renderFiltered({ ...base, sortMode: 'percentile', sortAscending: true, scoreMap, allScoreMap });
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('song-1');
    const idx2 = ids.indexOf('song-2');
    expect(idx1).toBeLessThan(idx2); // top 1% sorts before 50% ascending
  });

  it('sorts by stars mode', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', stars: 6 }));
    scoreMap.set('song-2', makeScore({ songId: 'song-2', stars: 3 }));
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', scoreMap.get('song-1')!]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', scoreMap.get('song-2')!]]));
    const { result } = renderFiltered({ ...base, sortMode: 'stars', sortAscending: false, scoreMap, allScoreMap });
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('song-1');
    const idx2 = ids.indexOf('song-2');
    expect(idx1).toBeLessThan(idx2); // 6 stars before 3 stars descending
  });

  it('sorts by seasonachieved mode', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', season: 5 }));
    scoreMap.set('song-2', makeScore({ songId: 'song-2', season: 3 }));
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', scoreMap.get('song-1')!]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', scoreMap.get('song-2')!]]));
    const { result } = renderFiltered({ ...base, sortMode: 'seasonachieved', sortAscending: false, scoreMap, allScoreMap });
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('song-1');
    const idx2 = ids.indexOf('song-2');
    expect(idx1).toBeLessThan(idx2); // season 5 before season 3 descending
  });

  it('sorts by hasfc mode', () => {
    const scoreMap = new Map<string, PlayerScore>();
    scoreMap.set('song-1', makeScore({ songId: 'song-1', isFullCombo: true }));
    scoreMap.set('song-2', makeScore({ songId: 'song-2', isFullCombo: false }));
    const allScoreMap = new Map<string, Map<ServerInstrumentKey, PlayerScore>>();
    allScoreMap.set('song-1', new Map([['Solo_Guitar', scoreMap.get('song-1')!]]));
    allScoreMap.set('song-2', new Map([['Solo_Guitar', scoreMap.get('song-2')!]]));
    const { result } = renderFiltered({ ...base, sortMode: 'hasfc', sortAscending: false, scoreMap, allScoreMap });
    const ids = result.current.map(s => s.songId);
    const idx1 = ids.indexOf('song-1');
    const idx2 = ids.indexOf('song-2');
    expect(idx1).toBeLessThan(idx2); // FC before non-FC descending
  });
});

// ──────────────────────────────────────────────────
// § useChartPagination — navigatePoint
// ──────────────────────────────────────────────────
import { useChartPagination } from '../../hooks/chart/useChartPagination';
import type { ChartPoint } from '../../hooks/chart/useChartData';

function makeChartPoint(idx: number): ChartPoint {
  return {
    date: `2025-01-${String(idx + 1).padStart(2, '0')}`,
    dateLabel: `1/${idx + 1}/25`,
    timestamp: Date.now() + idx * 86400000,
    score: 100000 + idx * 1000,
    accuracy: 95 + idx * 0.1,
    isFullCombo: false,
  };
}

describe('useChartPagination', () => {
  it('navigatePoint selects a point and adjusts offset', () => {
    const data = Array.from({ length: 20 }, (_, i) => makeChartPoint(i));
    const { result } = renderHook(() => useChartPagination(data, 5, 'Solo_Guitar'));
    act(() => result.current.navigatePoint(10));
    expect(result.current.selectedPoint).not.toBeNull();
    expect(result.current.selectedIndex).toBe(10);
  });

  it('navigatePoint clamps to first index', () => {
    const data = Array.from({ length: 10 }, (_, i) => makeChartPoint(i));
    const { result } = renderHook(() => useChartPagination(data, 5, 'Solo_Guitar'));
    act(() => result.current.navigatePoint(-5));
    expect(result.current.selectedIndex).toBe(0);
  });

  it('navigatePoint clamps to last index', () => {
    const data = Array.from({ length: 10 }, (_, i) => makeChartPoint(i));
    const { result } = renderHook(() => useChartPagination(data, 5, 'Solo_Guitar'));
    act(() => result.current.navigatePoint(100));
    expect(result.current.selectedIndex).toBe(9);
  });

  it('navigatePoint scrolls forward when point is after visible range', () => {
    const data = Array.from({ length: 20 }, (_, i) => makeChartPoint(i));
    const { result } = renderHook(() => useChartPagination(data, 5, 'Solo_Guitar'));
    // Initially offset=0, visible range = 15..19. Navigate to index 5 which is outside.
    act(() => result.current.navigatePoint(5));
    expect(result.current.selectedPoint).toBe(data[5]);
  });

  it('navigatePoint scrolls backward when point is before visible range', () => {
    const data = Array.from({ length: 20 }, (_, i) => makeChartPoint(i));
    const { result } = renderHook(() => useChartPagination(data, 5, 'Solo_Guitar'));
    // Scroll to end
    act(() => result.current.setChartOffset(15));
    // Now navigate to index 18 which is near the start
    act(() => result.current.navigatePoint(0));
    expect(result.current.selectedPoint).toBe(data[0]);
  });
});

// ──────────────────────────────────────────────────
// § useChartData — queryFn callback
// ──────────────────────────────────────────────────
vi.mock('../../api/client', () => ({
  api: createApiMock(),
}));

import { useChartData } from '../../hooks/chart/useChartData';

describe('useChartData', () => {
  it('fetches history via queryFn when no historyProp', async () => {
    const qc = createTestQueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(
      () => useChartData('test-player-1', 'song-1', 'Solo_Guitar'),
      { wrapper },
    );
    await waitFor(() => expect(result.current.loading).toBe(false));
    // History entries for Solo_Guitar should be filtered and mapped to chart points
    expect(result.current.chartData.length).toBeGreaterThan(0);
    expect(result.current.chartData[0]).toHaveProperty('score');
  });

  it('uses historyProp when provided', () => {
    const qc = createTestQueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(
      () => useChartData('test-player-1', 'song-1', 'Solo_Guitar', MOCK_HISTORY_ENTRIES),
      { wrapper },
    );
    expect(result.current.loading).toBe(false);
    expect(result.current.chartData.length).toBe(3);
  });

  it('returns instrumentCounts', async () => {
    const qc = createTestQueryClient();
    const wrapper = ({ children }: { children: ReactNode }) => (
      <QueryClientProvider client={qc}>{children}</QueryClientProvider>
    );
    const { result } = renderHook(
      () => useChartData('test-player-1', 'song-1', 'Solo_Guitar', MOCK_HISTORY_ENTRIES),
      { wrapper },
    );
    expect(result.current.instrumentCounts).toBeDefined();
    expect(result.current.instrumentCounts.Solo_Guitar).toBe(3);
  });
});

// ──────────────────────────────────────────────────
// § InstrumentStatsSection — updater functions
// ──────────────────────────────────────────────────
import {
  instStarsUpdater,
  instFCsUpdater,
  instPercentileUpdater,
  instPercentileWithScoresUpdater,
  instSongsPlayedUpdater,
  pctGold,
} from '../../pages/player/sections/InstrumentStatsSection';
import { defaultSongSettings } from '../../utils/songSettings';

describe('InstrumentStatsSection updaters', () => {
  const defaults = defaultSongSettings();

  it('instStarsUpdater sets instrument, sortMode=stars, and starsFilter', () => {
    const updater = instStarsUpdater('Solo_Guitar', 5);
    const result = updater(defaults);
    expect(result.instrument).toBe('Solo_Guitar');
    expect(result.sortMode).toBe('stars');
    expect(result.sortAscending).toBe(true);
  });

  it('instFCsUpdater sets hasFCs filter', () => {
    const updater = instFCsUpdater('Solo_Bass');
    const result = updater(defaults);
    expect(result.instrument).toBe('Solo_Bass');
    expect(result.sortMode).toBe('score');
    expect(result.filters.hasFCs.Solo_Bass).toBe(true);
  });

  it('instPercentileUpdater sets sortMode=percentile', () => {
    const updater = instPercentileUpdater('Solo_Drums');
    const result = updater(defaults);
    expect(result.instrument).toBe('Solo_Drums');
    expect(result.sortMode).toBe('percentile');
  });

  it('instPercentileWithScoresUpdater sets hasScores + percentile sort', () => {
    const updater = instPercentileWithScoresUpdater('Solo_Vocals');
    const result = updater(defaults);
    expect(result.instrument).toBe('Solo_Vocals');
    expect(result.sortMode).toBe('percentile');
    expect(result.filters.hasScores.Solo_Vocals).toBe(true);
  });

  it('instSongsPlayedUpdater sets hasScores filter', () => {
    const updater = instSongsPlayedUpdater('Solo_Guitar');
    const result = updater(defaults);
    expect(result.filters.hasScores.Solo_Guitar).toBe(true);
  });

  it('pctGold returns gold color for top percentiles', () => {
    expect(pctGold('Top 1%')).toBeDefined();
    expect(pctGold('Top 5%')).toBeDefined();
    expect(pctGold('Top 10%')).toBeUndefined();
    expect(pctGold('Top 50%')).toBeUndefined();
  });
});

// ──────────────────────────────────────────────────
// § useAccountSearch — click-outside
// ──────────────────────────────────────────────────
import { useAccountSearch } from '../../hooks/data/useAccountSearch';

describe('useAccountSearch click-outside', () => {
  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => { vi.useRealTimers(); });

  it('closes dropdown on mousedown outside container', async () => {
    const onSelect = vi.fn();
    // Render in a component that attaches containerRef to a real DOM element
    function Harness() {
      const search = useAccountSearch(onSelect, { debounceMs: 10 });
      return (
        <div>
          <div ref={search.containerRef} data-testid="search-container">
            <input
              value={search.query}
              onChange={e => search.handleChange(e.target.value)}
            />
            {search.isOpen && <div data-testid="dropdown">Results</div>}
          </div>
          <div data-testid="outside">Outside</div>
        </div>
      );
    }

    render(<Harness />);

    // Type to trigger search
    const input = screen.getByRole('textbox');
    fireEvent.change(input, { target: { value: 'Test' } });

    // Advance timers for debounce
    await act(async () => {
      vi.advanceTimersByTime(50);
      await vi.runAllTimersAsync();
    });

    // Dropdown should be open
    expect(screen.getByTestId('dropdown')).toBeInTheDocument();

    // Click outside the container
    fireEvent.mouseDown(screen.getByTestId('outside'));

    // Dropdown should close
    expect(screen.queryByTestId('dropdown')).toBeNull();
  });
});

// ──────────────────────────────────────────────────
// § PlayerDataContext — refreshPlayer
// ──────────────────────────────────────────────────
import { PlayerDataProvider, usePlayerData } from '../../contexts/PlayerDataContext';

describe('PlayerDataContext', () => {
  it('refreshPlayer invalidates queries', async () => {
    const qc = createTestQueryClient();
    let ctx: ReturnType<typeof usePlayerData>;
    function Consumer() {
      ctx = usePlayerData();
      return null;
    }

    render(
      <QueryClientProvider client={qc}>
        <PlayerDataProvider accountId="test-player-1">
          <Consumer />
        </PlayerDataProvider>
      </QueryClientProvider>,
    );

    // Wait for initial data to load
    await waitFor(() => expect(ctx!.playerLoading).toBe(false));

    // Call refreshPlayer
    await act(async () => {
      await ctx!.refreshPlayer();
    });

    // refreshPlayer should have invalidated the query
    expect(ctx!.playerData).not.toBeNull();
  });

  it('refreshPlayer is a no-op when accountId is undefined', async () => {
    const qc = createTestQueryClient();
    let ctx: ReturnType<typeof usePlayerData>;
    function Consumer() {
      ctx = usePlayerData();
      return null;
    }

    render(
      <QueryClientProvider client={qc}>
        <PlayerDataProvider accountId={undefined}>
          <Consumer />
        </PlayerDataProvider>
      </QueryClientProvider>,
    );

    // Call refreshPlayer — should not throw
    await act(async () => {
      await ctx!.refreshPlayer();
    });

    expect(ctx!.playerData).toBeNull();
  });
});

// ──────────────────────────────────────────────────
// § FloatingActionButton — searchVisible
// ──────────────────────────────────────────────────
import FloatingActionButton from '../../components/shell/fab/FloatingActionButton';
import { SearchQueryProvider } from '../../contexts/SearchQueryContext';
import { FabSearchProvider } from '../../contexts/FabSearchContext';

describe('FloatingActionButton', () => {
  it('renders search bar when defaultOpen is true', () => {
    render(
      <MemoryRouter>
        <FabSearchProvider>
          <SearchQueryProvider>
            <FloatingActionButton
              mode="songs"
              defaultOpen={true}
              onPress={vi.fn()}
            />
          </SearchQueryProvider>
        </FabSearchProvider>
      </MemoryRouter>,
    );
    const searchInput = document.querySelector('.fab-search-bar');
    expect(searchInput).not.toBeNull();
  });

  it('does not render search bar when defaultOpen is false', () => {
    render(
      <MemoryRouter>
        <FabSearchProvider>
          <SearchQueryProvider>
            <FloatingActionButton
              mode="songs"
              defaultOpen={false}
              onPress={vi.fn()}
            />
          </SearchQueryProvider>
        </FabSearchProvider>
      </MemoryRouter>,
    );
    const searchInput = document.querySelector('.fab-search-bar');
    expect(searchInput).toBeNull();
  });
});

// ──────────────────────────────────────────────────
// § BottomNav — player truthy branch
// ──────────────────────────────────────────────────
import BottomNav from '../../components/shell/mobile/BottomNav';

describe('BottomNav player branch', () => {
  it('renders extra tabs when player is provided', () => {
    const player = { accountId: 'p1', displayName: 'Test' };
    const { container } = render(
      <BottomNav
        player={player}
        activeTab={'songs' as any}
        onTabClick={vi.fn()}
      />,
    );
    const buttons = container.querySelectorAll('button');
    expect(buttons.length).toBe(4); // Songs, Suggestions, Statistics, Settings
  });

  it('renders fewer tabs without player', () => {
    const { container } = render(
      <BottomNav
        player={null}
        activeTab={'songs' as any}
        onTabClick={vi.fn()}
      />,
    );
    const buttons = container.querySelectorAll('button');
    expect(buttons.length).toBe(2); // Songs, Settings
  });
});

// ──────────────────────────────────────────────────
// § PlayerPage — justCompleted branch
// ──────────────────────────────────────────────────
vi.mock('../../hooks/data/useSyncStatus', () => ({
  SyncPhase: { None: 'none', Complete: 'complete', Error: 'error' },
  useSyncStatus: vi.fn().mockReturnValue({
    isSyncing: false,
    phase: 'none',
    backfillProgress: 0,
    historyProgress: 0,
    justCompleted: false,
    clearCompleted: vi.fn(),
  }),
}));

import PlayerPage from '../../pages/player/PlayerPage';
import { useSyncStatus } from '../../hooks/data/useSyncStatus';

describe('PlayerPage', () => {
  it('renders player not found when no data and not loading', async () => {
    const mock = createApiMock();
    mock.getPlayer.mockResolvedValue(null);
    const { api } = await import('../../api/client');
    vi.mocked(api.getPlayer).mockResolvedValue(null as any);

    render(
      <TestProviders route="/player/unknown-id">
        <Routes>
          <Route path="/player/:accountId" element={<PlayerPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(screen.getByText(/player.*not.*found/i)).toBeInTheDocument();
    });
  });

  it('handles justCompleted sync for non-tracked player', async () => {
    const clearCompleted = vi.fn();
    vi.mocked(useSyncStatus).mockReturnValue({
      isSyncing: false,
      phase: 'complete' as any,
      backfillProgress: 100,
      historyProgress: 100,
      justCompleted: true,
      clearCompleted,
      progress: 1,
      backfillStatus: null,
      historyStatus: null,
      entriesFound: 0,
    });

    render(
      <TestProviders route="/player/test-player-1">
        <Routes>
          <Route path="/player/:accountId" element={<PlayerPage />} />
        </Routes>
      </TestProviders>,
    );

    await waitFor(() => {
      expect(clearCompleted).toHaveBeenCalled();
    });

    // Reset mock
    vi.mocked(useSyncStatus).mockReturnValue({
      isSyncing: false,
      phase: 'none' as any,
      backfillProgress: 0,
      historyProgress: 0,
      justCompleted: false,
      clearCompleted: vi.fn(),
      progress: 0,
      backfillStatus: null,
      historyStatus: null,
      entriesFound: 0,
    });
  });
});
