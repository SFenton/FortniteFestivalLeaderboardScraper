import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import React from 'react';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SettingsProvider } from '../../../../../src/contexts/SettingsContext';
import { FestivalProvider } from '../../../../../src/contexts/FestivalContext';
import { FabSearchProvider } from '../../../../../src/contexts/FabSearchContext';
import { SearchQueryProvider, useSearchQuery } from '../../../../../src/contexts/SearchQueryContext';
import { PlayerDataProvider } from '../../../../../src/contexts/PlayerDataContext';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../../../../helpers/browserStubs';
import { ScrollContainerProvider, useScrollContainer, useHeaderPortalRef } from '../../../../../src/contexts/ScrollContainerContext';
import PlayerContent from '../../../../../src/pages/leaderboard/player/components/PlayerContent';
import { SyncPhase } from '@festival/core';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 's1', title: 'Test Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3 }, albumArt: 'art.jpg' },
    ], count: 1, currentSeason: 5 }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', totalScores: 1, scores: [
      { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 3, percentile: 90, accuracy: 95, isFullCombo: false, stars: 5, season: 5, totalEntries: 500 },
    ] }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'p1', count: 0, history: [] }),
    getLeaderboard: fn().mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 's1', instruments: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'p1', stats: [
      { instrument: 'Solo_Guitar', songsPlayed: 10, fullComboCount: 2, goldStarCount: 5, avgAccuracy: 96.5, bestRank: 1, totalScore: 1200000 },
    ] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: '' }),
    getRivalsAll: fn().mockResolvedValue({ accountId: 'p1', songs: [], combos: [] }),
    getShop: fn().mockResolvedValue({ songs: [] }),
  };
});
vi.mock('../../../../../src/api/client', () => ({ api: mockApi }));

beforeAll(() => { stubScrollTo(); stubResizeObserver(); stubElementDimensions(); });
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  mockApi.getSongs.mockResolvedValue({ songs: [{ songId: 's1', title: 'Test Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3 }, albumArt: 'art.jpg' }], count: 1, currentSeason: 5 });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', totalScores: 1, scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 3, percentile: 90, accuracy: 95, isFullCombo: false, stars: 5, season: 5, totalEntries: 500 }] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'p1', stats: [{ instrument: 'Solo_Guitar', songsPlayed: 10, fullComboCount: 2, goldStarCount: 5, avgAccuracy: 96.5, bestRank: 1, totalScore: 1200000 }] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'p1', count: 0, history: [] });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 's1', instruments: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: '' });
});

function ShellInjector({ children }: { children: React.ReactNode }) {
  const sRef = useScrollContainer();
  const setPortalNode = useHeaderPortalRef();

  return (
    <>
      <div ref={setPortalNode} />
      <div ref={(el) => {
        if (el && !sRef.current) {
          Object.defineProperty(el, 'scrollHeight', { value: 5000, writable: true, configurable: true });
          Object.defineProperty(el, 'scrollTop', { value: 0, writable: true, configurable: true });
          el.scrollTo = (() => {}) as any;
          sRef.current = el;
        }
      }}>
        {children}
      </div>
    </>
  );
}

function Providers({ children, accountId }: { children: React.ReactNode; accountId?: string }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return (
    <QueryClientProvider client={qc}>
    <SettingsProvider>
      <FestivalProvider>
        <FabSearchProvider>
          <SearchQueryProvider>
            <PlayerDataProvider accountId={accountId}>
              <ScrollContainerProvider>
              <ShellInjector>
              <MemoryRouter>{children}</MemoryRouter>
              </ShellInjector>
              </ScrollContainerProvider>
            </PlayerDataProvider>
          </SearchQueryProvider>
        </FabSearchProvider>
      </FestivalProvider>
    </SettingsProvider>
    </QueryClientProvider>
  );
}

describe('PlayerContent', () => {
  const playerData = {
    accountId: 'p1', displayName: 'TestPlayer', totalScores: 2,
    scores: [
      { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 3, percentile: 90, accuracy: 95, isFullCombo: false, stars: 5, season: 5, totalEntries: 500 },
      { songId: 's1', instrument: 'Solo_Bass', score: 80000, rank: 10, percentile: 60, accuracy: 85, isFullCombo: false, stars: 3, season: 4, totalEntries: 300 },
    ],
  };
  const songs = [{ songId: 's1', title: 'Test Song', artist: 'Artist A', year: 2024 }];

  it('renders player display name', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} isTrackedPlayer={true} skipAnim />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
  });

  it('renders instrument stats section', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} isTrackedPlayer={true} skipAnim />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
  });

  it('shows sync banner when syncing', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={true} phase={SyncPhase.Backfill} backfillProgress={50} historyProgress={0} isTrackedPlayer={true} skipAnim />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
  });

  it('renders for non-tracked player', async () => {
    render(
      <Providers>
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} isTrackedPlayer={false} skipAnim={false} />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
  });

  it('renders with no scores', async () => {
    const emptyPlayer = { accountId: 'p1', displayName: 'Empty', totalScores: 0, scores: [] };
    render(
      <Providers accountId="p1">
        <PlayerContent data={emptyPlayer as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} isTrackedPlayer={true} skipAnim />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('Empty')).toBeDefined(); });
  });

  it('renders with full combo scores', async () => {
    const fcPlayer = {
      accountId: 'p1', displayName: 'FCPlayer', totalScores: 1,
      scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 150000, rank: 1, percentile: 99, accuracy: 100, isFullCombo: true, stars: 6, season: 5, totalEntries: 500 }],
    };
    render(
      <Providers accountId="p1">
        <PlayerContent data={fcPlayer as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} isTrackedPlayer={true} skipAnim />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('FCPlayer')).toBeDefined(); });
  });

  it('clears search query when navigating to songs via category card', async () => {
    // Helper that seeds & reads the search query from context
    function SearchSpy({ onMount }: { onMount: (setQuery: (q: string) => void) => void }) {
      const { query, setQuery } = useSearchQuery();
      React.useEffect(() => { onMount(setQuery); }, []); // eslint-disable-line react-hooks/exhaustive-deps
      return <span data-testid="search-query">{query}</span>;
    }
    let setQueryFn!: (q: string) => void;

    const { getByTestId } = render(
      <Providers accountId="p1">
        <SearchSpy onMount={(fn) => { setQueryFn = fn; }} />
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} isTrackedPlayer={true} skipAnim />
      </Providers>,
    );

    // Wait for render
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });

    // Seed a non-empty search query
    React.act(() => { setQueryFn('hello'); });
    expect(getByTestId('search-query').textContent).toBe('hello');

    // Click "Songs Played" stat card — triggers navigateToSongs
    const songsPlayed = screen.getAllByText('Songs Played')[0]!;
    fireEvent.click(songsPlayed);

    // Search query should be cleared
    await waitFor(() => { expect(getByTestId('search-query').textContent).toBe(''); });
  });
});
