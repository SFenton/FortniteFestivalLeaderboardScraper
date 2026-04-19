import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import React from 'react';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SettingsProvider } from '../../../../../src/contexts/SettingsContext';
import { FeatureFlagsProvider } from '../../../../../src/contexts/FeatureFlagsContext';
import { FestivalProvider } from '../../../../../src/contexts/FestivalContext';
import { FabSearchProvider } from '../../../../../src/contexts/FabSearchContext';
import { SearchQueryProvider, useSearchQuery } from '../../../../../src/contexts/SearchQueryContext';
import { PlayerDataProvider } from '../../../../../src/contexts/PlayerDataContext';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { stubMatchMedia, stubScrollTo, stubResizeObserver, stubElementDimensions } from '../../../../helpers/browserStubs';
import { ScrollContainerProvider, useScrollContainer, useHeaderPortalRef } from '../../../../../src/contexts/ScrollContainerContext';
import PlayerContent from '../../../../../src/pages/leaderboard/player/components/PlayerContent';
import { SyncPhase } from '@festival/core';

let mockIsWideDesktop = true;

vi.mock('../../../../../src/hooks/ui/useIsMobile', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../../src/hooks/ui/useIsMobile')>();
  return {
    ...actual,
    useIsMobile: () => false,
    useIsWideDesktop: () => mockIsWideDesktop,
  };
});

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
  mockIsWideDesktop = true;
  stubMatchMedia(false);
  mockApi.getSongs.mockResolvedValue({ songs: [{ songId: 's1', title: 'Test Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3 }, albumArt: 'art.jpg' }], count: 1, currentSeason: 5 });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', totalScores: 1, scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 3, percentile: 90, accuracy: 95, isFullCombo: false, stars: 5, season: 5, totalEntries: 500 }] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null, rivals: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'p1', stats: [{ instrument: 'Solo_Guitar', songsPlayed: 10, fullComboCount: 2, goldStarCount: 5, avgAccuracy: 96.5, bestRank: 1, totalScore: 1200000 }] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'p1', count: 0, history: [] });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 's1', instruments: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: '' });
});

function setDynamicSectionRect(element: HTMLElement, absoluteTop: number, shell: HTMLElement) {
  Object.defineProperty(element, 'getBoundingClientRect', {
    configurable: true,
    value: () => ({
      top: absoluteTop - shell.scrollTop,
      left: 0,
      bottom: absoluteTop - shell.scrollTop + 60,
      right: 600,
      width: 600,
      height: 60,
      x: 0,
      y: absoluteTop - shell.scrollTop,
      toJSON() { return this; },
    }),
  });
}

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
      <FeatureFlagsProvider>
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
      </FeatureFlagsProvider>
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
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
  });

  it('renders instrument stats section', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
  });

  it('shows sync banner when syncing', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={true} phase={SyncPhase.Backfill} backfillProgress={50} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
  });

  it('renders for non-tracked player', async () => {
    render(
      <Providers>
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={false} skipAnim={false} statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
  });

  it('renders with no scores', async () => {
    const emptyPlayer = { accountId: 'p1', displayName: 'Empty', totalScores: 0, scores: [] };
    render(
      <Providers accountId="p1">
        <PlayerContent data={emptyPlayer as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
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
        <PlayerContent data={fcPlayer as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );
    await waitFor(() => { expect(screen.getByText('FCPlayer')).toBeDefined(); });
  });

  it('renders the player bands section when the flag is enabled', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent
          data={playerData as any}
          songs={songs as any}
          isSyncing={false}
          phase={SyncPhase.Idle}
          backfillProgress={0}
          historyProgress={0}
          rivalsProgress={0}
          itemsCompleted={0}
          totalItems={0}
          entriesFound={0}
          currentSongName={null}
          seasonsQueried={0}
          rivalsFound={0}
          isTrackedPlayer={true}
          skipAnim
          statsData={{
            accountId: 'p1',
            totalSongs: 1,
            instruments: [],
            bands: {
              all: {
                totalCount: 6,
                entries: [{
                  teamKey: 'p1:p2',
                  bandType: 'Band_Duets',
                  members: [
                    { accountId: 'p1', displayName: 'TestPlayer', instruments: ['Solo_Guitar'] },
                    { accountId: 'p2', displayName: 'BandMate', instruments: ['Solo_Bass'] },
                  ],
                }],
              },
              duos: {
                totalCount: 6,
                entries: [{
                  teamKey: 'p1:p2',
                  bandType: 'Band_Duets',
                  members: [
                    { accountId: 'p1', displayName: 'TestPlayer', instruments: ['Solo_Guitar'] },
                    { accountId: 'p2', displayName: 'BandMate', instruments: ['Solo_Bass'] },
                  ],
                }],
              },
              trios: { totalCount: 0, entries: [] },
              quads: { totalCount: 0, entries: [] },
            },
          } as any}
          rankingQueryResults={[]}
        />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByText("TestPlayer's Bands")).toBeDefined();
    });
      expect(screen.getAllByText('BandMate')).toHaveLength(2);
      expect(screen.getAllByText('View all bands (6)')).toHaveLength(2);
  });

  it('hides the player bands section when the flag is disabled', async () => {
    localStorage.setItem('fst:featureFlagOverrides', JSON.stringify({ playerBands: false }));

    render(
      <Providers accountId="p1">
        <PlayerContent
          data={playerData as any}
          songs={songs as any}
          isSyncing={false}
          phase={SyncPhase.Idle}
          backfillProgress={0}
          historyProgress={0}
          rivalsProgress={0}
          itemsCompleted={0}
          totalItems={0}
          entriesFound={0}
          currentSongName={null}
          seasonsQueried={0}
          rivalsFound={0}
          isTrackedPlayer={true}
          skipAnim
          statsData={{
            accountId: 'p1',
            totalSongs: 1,
            instruments: [],
            bands: {
              all: { totalCount: 1, entries: [] },
              duos: { totalCount: 1, entries: [] },
              trios: { totalCount: 0, entries: [] },
              quads: { totalCount: 0, entries: [] },
            },
          } as any}
          rankingQueryResults={[]}
        />
      </Providers>,
    );

    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
    expect(screen.queryByText("TestPlayer's Bands")).toBeNull();
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
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
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

  it('renders quick links in wide desktop mode', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByRole('navigation', { name: 'Quick Links' })).toBeDefined();
    });

    expect(screen.getByRole('navigation', { name: 'Quick Links' })).toHaveStyle({ position: 'sticky', top: '0px' });
    expect(screen.queryByText('Quick Links')).toBeNull();
    expect(screen.getByTestId('player-quick-link-global')).toBeDefined();
    expect(screen.getByTestId('player-quick-link-instrument-solo-guitar')).toBeDefined();
    expect(screen.getByTestId('player-quick-link-instrument-solo-bass')).toBeDefined();
    expect(screen.getByTestId('player-quick-link-top-songs')).toBeDefined();
  });

  it('hides quick links outside wide desktop mode', async () => {
    mockIsWideDesktop = false;

    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => { expect(screen.getByText('TestPlayer')).toBeDefined(); });
    expect(screen.queryByRole('navigation', { name: 'Quick Links' })).toBeNull();
  });

  it('shows the bands quick link only when the bands section is enabled', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent
          data={playerData as any}
          songs={songs as any}
          isSyncing={false}
          phase={SyncPhase.Idle}
          backfillProgress={0}
          historyProgress={0}
          rivalsProgress={0}
          itemsCompleted={0}
          totalItems={0}
          entriesFound={0}
          currentSongName={null}
          seasonsQueried={0}
          rivalsFound={0}
          isTrackedPlayer={true}
          skipAnim
          statsData={{
            accountId: 'p1',
            totalSongs: 1,
            instruments: [],
            bands: {
              all: { totalCount: 1, entries: [] },
              duos: { totalCount: 1, entries: [] },
              trios: { totalCount: 0, entries: [] },
              quads: { totalCount: 0, entries: [] },
            },
          } as any}
          rankingQueryResults={[]}
        />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-bands')).toBeDefined();
    });
  });

  it('scrolls the shell container when a quick link is clicked', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-top-songs')).toBeDefined();
    });

    const shell = screen.getByTestId('page-root').parentElement as HTMLElement;
    const topSongsSection = screen.getByTestId('player-section-top-songs');
    const scrollToSpy = vi.fn();
    shell.scrollTo = scrollToSpy as any;
    shell.scrollTop = 0;
    setDynamicSectionRect(topSongsSection, 640, shell);

    fireEvent.click(screen.getByTestId('player-quick-link-top-songs'));

    expect(scrollToSpy).toHaveBeenCalledWith({ top: 632, behavior: 'smooth' });
  });

  it('updates the active quick link as the shell scroll position changes', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-global')).toBeDefined();
    });

    const shell = screen.getByTestId('page-root').parentElement as HTMLElement;
    const globalSection = screen.getByTestId('player-section-global');
    const guitarSection = screen.getByTestId('player-section-instrument-solo-guitar');
    const bassSection = screen.getByTestId('player-section-instrument-solo-bass');
    const topSongsSection = screen.getByTestId('player-section-top-songs');

    setDynamicSectionRect(globalSection, 80, shell);
    setDynamicSectionRect(guitarSection, 520, shell);
    setDynamicSectionRect(bassSection, 760, shell);
    setDynamicSectionRect(topSongsSection, 980, shell);

    shell.scrollTop = 0;
    fireEvent.scroll(shell);

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-global')).toHaveAttribute('aria-current', 'location');
    });

    shell.scrollTop = 600;
    fireEvent.scroll(shell);

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-instrument-solo-guitar')).toHaveAttribute('aria-current', 'location');
    });

    shell.scrollTop = 1100;
    fireEvent.scroll(shell);

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-top-songs')).toHaveAttribute('aria-current', 'location');
    });
  });
});
