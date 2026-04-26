import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import React from 'react';
import { render, screen, waitFor, fireEvent } from '@testing-library/react';
import { MemoryRouter, useLocation } from 'react-router-dom';
import { SettingsProvider } from '../../../../../src/contexts/SettingsContext';
import { FeatureFlagsProvider } from '../../../../../src/contexts/FeatureFlagsContext';
import { FestivalProvider } from '../../../../../src/contexts/FestivalContext';
import { FabSearchProvider } from '../../../../../src/contexts/FabSearchContext';
import { PageQuickLinksProvider, usePageQuickLinksController } from '../../../../../src/contexts/PageQuickLinksContext';
import { SearchQueryProvider, useSearchQuery } from '../../../../../src/contexts/SearchQueryContext';
import { PlayerDataProvider } from '../../../../../src/contexts/PlayerDataContext';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { stubMatchMedia, stubScrollTo, stubResizeObserver, stubElementDimensions } from '../../../../helpers/browserStubs';
import { ScrollContainerProvider, useScrollContainer, useHeaderPortalRef, useQuickLinksRailPortalRef } from '../../../../../src/contexts/ScrollContainerContext';
import { DEFAULT_QUICK_LINK_SCROLL_OFFSET } from '../../../../../src/hooks/ui/usePageQuickLinks';
import PlayerContentBase from '../../../../../src/pages/leaderboard/player/components/PlayerContent';
import { SyncPhase } from '@festival/core';
import { IconSize, Layout } from '@festival/theme';

const PlayerContent = PlayerContentBase as unknown as (props: any) => React.JSX.Element;

let mockIsWideDesktop = true;
let mockHasFab = false;

vi.mock('../../../../../src/hooks/ui/useIsMobile', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../../../../src/hooks/ui/useIsMobile')>();
  return {
    ...actual,
    useIsMobile: () => mockHasFab,
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
  mockHasFab = false;
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

function setDynamicRect(element: HTMLElement, absoluteTop: number, height: number, shell: HTMLElement) {
  Object.defineProperty(element, 'getBoundingClientRect', {
    configurable: true,
    value: () => ({
      top: absoluteTop - shell.scrollTop,
      left: 0,
      bottom: absoluteTop - shell.scrollTop + height,
      right: 600,
      width: 600,
      height,
      x: 0,
      y: absoluteTop - shell.scrollTop,
      toJSON() { return this; },
    }),
  });
}

function stubIntersectingObserver() {
  const OriginalIntersectionObserver = globalThis.IntersectionObserver;

  class MockIntersectionObserver {
    private readonly callback: IntersectionObserverCallback;

    constructor(callback: IntersectionObserverCallback) {
      this.callback = callback;
    }

    observe(target: Element) {
      const rect = target.getBoundingClientRect();
      this.callback([
        {
          target,
          isIntersecting: true,
          intersectionRatio: 1,
          time: 0,
          boundingClientRect: rect,
          intersectionRect: rect,
          rootBounds: null,
        } as IntersectionObserverEntry,
      ], this as unknown as IntersectionObserver);
    }

    unobserve() {}
    disconnect() {}
    readonly root = null;
    readonly rootMargin = '';
    readonly thresholds = [] as readonly number[];
    takeRecords(): IntersectionObserverEntry[] { return []; }
  }

  globalThis.IntersectionObserver = MockIntersectionObserver as unknown as typeof globalThis.IntersectionObserver;

  return () => {
    globalThis.IntersectionObserver = OriginalIntersectionObserver;
  };
}

function ShellInjector({ children }: { children: React.ReactNode }) {
  const sRef = useScrollContainer();
  const setPortalNode = useHeaderPortalRef();
  const setQuickLinksRailNode = useQuickLinksRailPortalRef();

  return (
    <>
      <div ref={setPortalNode} />
      <div ref={(el) => {
        if (el && !sRef.current) {
                    Object.defineProperty(el, 'clientHeight', { value: 540, writable: true, configurable: true });
          Object.defineProperty(el, 'scrollHeight', { value: 5000, writable: true, configurable: true });
          Object.defineProperty(el, 'scrollTop', { value: 0, writable: true, configurable: true });
          el.scrollTo = (() => {}) as any;
          sRef.current = el;
        }
      }} data-testid="test-scroll-container">
        {children}
      </div>
      <div ref={(el) => {
        if (el) {
          Object.defineProperty(el, 'clientHeight', { value: 620, writable: true, configurable: true });
          setQuickLinksRailNode(el);
          return;
        }

        setQuickLinksRailNode(null);
      }} data-testid="test-quick-links-portal" />
    </>
  );
}

function Providers({ children, accountId, route = '/' }: { children: React.ReactNode; accountId?: string; route?: string }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return (
    <QueryClientProvider client={qc}>
    <SettingsProvider>
      <FeatureFlagsProvider>
      <FestivalProvider>
        <FabSearchProvider>
          <PageQuickLinksProvider>
          <SearchQueryProvider>
            <PlayerDataProvider accountId={accountId}>
              <ScrollContainerProvider>
              <ShellInjector>
              <MemoryRouter initialEntries={[route]}>{children}</MemoryRouter>
              </ShellInjector>
              </ScrollContainerProvider>
            </PlayerDataProvider>
          </SearchQueryProvider>
          </PageQuickLinksProvider>
        </FabSearchProvider>
      </FestivalProvider>
      </FeatureFlagsProvider>
    </SettingsProvider>
    </QueryClientProvider>
  );
}

function LocationProbe() {
  const location = useLocation();
  const preserveShellScroll = !!(location.state as { preserveShellScrollKey?: string } | null)?.preserveShellScrollKey;
  return (
    <>
      <span data-testid="location-path">{location.pathname}</span>
      <span data-testid="location-preserve-scroll">{String(preserveShellScroll)}</span>
    </>
  );
}

function FabQuickLinksSpy() {
  const pageQuickLinks = usePageQuickLinksController();

  return (
    <>
      <span data-testid="fab-player-quick-links">{String(pageQuickLinks.hasPageQuickLinks)}</span>
      <button data-testid="fab-player-quick-links-open" onClick={() => pageQuickLinks.openPageQuickLinks()}>
        Open Quick Links
      </button>
    </>
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
  const playerStatsWithBands = {
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
  };

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
    expect(screen.getByTestId('player-bands-view-all')).toHaveAttribute('href', '/bands/player/p1?group=all&page=1&name=TestPlayer');
    expect(screen.queryByText('All Bands')).toBeNull();
    expect(screen.getByText('Duos')).toBeDefined();
    expect(screen.getAllByText('BandMate')).toHaveLength(1);
    expect(screen.getAllByText('View all bands (6)')).toHaveLength(1);
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

  it('uses a single-column player grid on mobile widths above the narrow breakpoint', async () => {
    mockIsWideDesktop = false;
    mockHasFab = true;
    stubMatchMedia(false);

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
          statsData={playerStatsWithBands as any}
          rankingQueryResults={[]}
        />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByText("TestPlayer's Bands")).toBeDefined();
    });

    expect(screen.getByTestId('player-grid-list').style.gridTemplateColumns).toBe('minmax(0, 1fr)');
  });

  it('keeps the two-column player grid on desktop widths', async () => {
    mockIsWideDesktop = true;
    mockHasFab = false;
    stubMatchMedia(false);

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
          statsData={playerStatsWithBands as any}
          rankingQueryResults={[]}
        />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByText("TestPlayer's Bands")).toBeDefined();
    });

    expect(screen.getByTestId('player-grid-list').style.gridTemplateColumns).toBe('repeat(2, minmax(0, 1fr))');
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

    const globalLink = screen.getByTestId('player-quick-link-global');
    const guitarLink = screen.getByTestId('player-quick-link-instrument-solo-guitar');
    const bassLink = screen.getByTestId('player-quick-link-instrument-solo-bass');
    const topSongsLink = screen.getByTestId('player-quick-link-top-songs');
    const quickLinksNav = screen.getByRole('navigation', { name: 'Quick Links' });
    const quickLinksRail = screen.getByTestId('player-quick-links-rail');
    const quickLinksPortal = screen.getByTestId('test-quick-links-portal');
    const scrollContainer = screen.getByTestId('test-scroll-container');

    expect(quickLinksPortal).toContainElement(quickLinksRail);
    expect(scrollContainer).not.toContainElement(quickLinksRail);
    expect(quickLinksNav).toHaveStyle({ overflowY: 'auto', overscrollBehavior: 'contain', maxHeight: '620px' });
    expect(screen.queryByText('Quick Links')).toBeNull();
    expect(globalLink).toBeDefined();
    expect(guitarLink).toBeDefined();
    expect(bassLink).toBeDefined();
    expect(topSongsLink).toBeDefined();
    expect(globalLink).toHaveStyle({ height: '48px', flexShrink: '0' });
    expect(guitarLink).toHaveStyle({ height: '48px', flexShrink: '0' });
    expect(globalLink).toHaveTextContent('Global Statistics');
    expect(guitarLink.textContent).not.toContain('Statistics');
    expect(bassLink.textContent).not.toContain('Statistics');
    expect(topSongsLink).toHaveTextContent('Top Songs');
    expect(globalLink.querySelector('svg')).not.toBeNull();
    const guitarIcon = guitarLink.querySelector('img');
    const bassIcon = bassLink.querySelector('img');
    const guitarIconSlot = guitarLink.querySelector('span[aria-hidden="true"]');
    expect(guitarIcon?.getAttribute('src')).toContain('guitar.png');
    expect(guitarIcon?.getAttribute('width')).toBe('20');
    expect(guitarIcon).toHaveStyle({ transform: 'scale(1.15)', transformOrigin: 'center' });
    expect(guitarIconSlot).toHaveStyle({ width: '20px' });
    expect(bassIcon?.getAttribute('src')).toContain('bass.png');
    expect(bassIcon?.getAttribute('width')).toBe('20');
    expect(bassIcon).toHaveStyle({ transform: 'scale(1.15)', transformOrigin: 'center' });
    expect(topSongsLink.querySelector('svg')).not.toBeNull();
  });

  it('delays the wide desktop rail reveal until the player grid stagger finishes', async () => {
    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim={false} statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByRole('navigation', { name: 'Quick Links' })).toBeDefined();
    });

    const quickLinksRail = screen.getByTestId('player-quick-links-rail');

    expect(quickLinksRail).toHaveStyle({ opacity: '0', pointerEvents: 'none' });
    expect(quickLinksRail.style.animation).toContain('fadeIn');
  });

  it('applies the shared page scroll fade on the player details page', async () => {
    const { container } = render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByText('TestPlayer')).toBeDefined();
    });

    const scrollContainer = screen.getByTestId('test-scroll-container');
    const pageRoot = screen.getByTestId('page-root');
    const scrollArea = container.querySelector('[data-testid="scroll-area"]');

    expect(scrollArea).toBeTruthy();

    Object.defineProperty(scrollContainer, 'getBoundingClientRect', {
      configurable: true,
      value: () => ({
        top: 0,
        left: 0,
        bottom: 540,
        right: 1024,
        width: 1024,
        height: 540,
        x: 0,
        y: 0,
        toJSON() { return this; },
      }),
    });

    Object.defineProperty(pageRoot, 'getBoundingClientRect', {
      configurable: true,
      value: () => ({
        top: 0,
        left: 0,
        bottom: 1280,
        right: 1024,
        width: 1024,
        height: 1280,
        x: 0,
        y: 0,
        toJSON() { return this; },
      }),
    });

    fireEvent.scroll(scrollContainer);

    await waitFor(() => {
      expect(pageRoot.style.maskImage).toContain('linear-gradient');
    });
  });

  it('applies per-item scroll fade to the percentile table when it re-enters from above', async () => {
    const restoreIntersectionObserver = stubIntersectingObserver();

    try {
      render(
        <Providers accountId="p1">
          <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
        </Providers>,
      );

      await waitFor(() => {
        expect(screen.getByText('TestPlayer')).toBeDefined();
      });

      const scrollContainer = screen.getByTestId('test-scroll-container');
      const percentileTable = screen.getByTestId('player-item-Solo_Guitar-pct-table');

      Object.defineProperty(scrollContainer, 'getBoundingClientRect', {
        configurable: true,
        value: () => ({
          top: 0,
          left: 0,
          bottom: 540,
          right: 1024,
          width: 1024,
          height: 540,
          x: 0,
          y: 0,
          toJSON() { return this; },
        }),
      });

      setDynamicRect(percentileTable, 180, 320, scrollContainer);
      Object.defineProperty(scrollContainer, 'scrollTop', { value: 200, writable: true, configurable: true });

      fireEvent.scroll(scrollContainer);

      await waitFor(() => {
        expect(percentileTable.style.maskImage).toContain('linear-gradient');
      });
    } finally {
      restoreIntersectionObserver();
    }
  });

  it('renders a labeled quick links trigger on compact desktop', async () => {
    mockIsWideDesktop = false;
    mockHasFab = false;

    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => { expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined(); });
    expect(screen.queryByRole('navigation', { name: 'Quick Links' })).toBeNull();
    const trigger = screen.getByRole('button', { name: 'Quick Links' });
    expect(trigger).toHaveTextContent('Quick Links');
    expect(trigger).toHaveStyle({
      backgroundColor: 'rgba(18, 24, 38, 0.78)',
      color: 'rgb(255, 255, 255)',
      fontSize: '12px',
      fontWeight: '600',
      height: '48px',
      paddingLeft: '12px',
      paddingRight: '12px',
    });
    expect(screen.queryByText('Select Player Profile')).toBeNull();
  });

  it('renders Select Player Profile with compact pill metrics and keeps Quick Links on the right', async () => {
    mockIsWideDesktop = false;
    mockHasFab = false;

    render(
      <Providers>
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={false} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
      expect(screen.getByRole('button', { name: 'Select Player Profile' })).toBeDefined();
    });

    const selectButton = screen.getByRole('button', { name: 'Select Player Profile' });
    expect(selectButton).toHaveStyle({
      backgroundColor: 'rgb(124, 58, 237)',
      color: 'rgb(255, 255, 255)',
      fontSize: '12px',
      fontWeight: '600',
      height: '48px',
      paddingLeft: '12px',
      paddingRight: '12px',
    });

    const actionButtons = Array.from(screen.getByTestId('player-header-actions').querySelectorAll('button'));
    expect(actionButtons).toHaveLength(2);
    expect(actionButtons[0]).toHaveAccessibleName('Select Player Profile');
    expect(actionButtons[1]).toHaveAccessibleName('Quick Links');
  });

  it('animates the select profile slot out so quick links can settle smoothly', async () => {
    mockIsWideDesktop = false;
    mockHasFab = false;

    const initialRender = render(
      <Providers>
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={false} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
      expect(screen.getByRole('button', { name: 'Select Player Profile' })).toBeDefined();
    });

    expect(screen.getByTestId('player-header-actions')).toHaveStyle({ gap: '8px' });
    expect(screen.getByTestId('player-select-profile-slot')).toHaveStyle({ maxWidth: '360px', overflow: 'hidden' });

    fireEvent.click(screen.getByRole('button', { name: 'Select Player Profile' }));

    const exitingSlot = screen.getByTestId('player-select-profile-slot');
    expect(screen.getByTestId('player-header-actions')).toHaveStyle({ gap: '0px' });
    expect(exitingSlot).toHaveStyle({ maxWidth: '0px', opacity: '0' });
    expect(exitingSlot.querySelector('button')).not.toBeNull();
    expect(JSON.parse(localStorage.getItem('fst:trackedPlayer') ?? 'null')).toMatchObject({ accountId: 'p1', displayName: 'TestPlayer' });

    initialRender.unmount();
    render(
      <Providers>
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={false} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    expect(screen.getByTestId('player-header-actions')).toHaveStyle({ gap: '0px' });
    const remountedExitSlot = screen.queryByTestId('player-select-profile-slot');
    if (remountedExitSlot) {
      expect(remountedExitSlot).toHaveStyle({ maxWidth: '0px', opacity: '0' });
    }

    await waitFor(() => {
      expect(screen.queryByTestId('player-select-profile-slot')).toBeNull();
    });
  });

  it('redirects to /statistics when selecting the viewed player as profile', async () => {
    mockIsWideDesktop = false;
    mockHasFab = false;

    render(
      <Providers route="/player/p1">
        <LocationProbe />
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={false} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Select Player Profile' })).toBeDefined();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Select Player Profile' }));

    await waitFor(() => {
      expect(screen.getByTestId('location-path').textContent).toBe('/statistics');
    });
    expect(screen.getByTestId('location-preserve-scroll').textContent).toBe('true');

    expect(JSON.parse(localStorage.getItem('fst:trackedPlayer') ?? 'null')).toMatchObject({
      accountId: 'p1',
      displayName: 'TestPlayer',
    });
  });

  it('renders a labeled quick links trigger on mobile using the same pill style', async () => {
    mockIsWideDesktop = false;
    mockHasFab = true;

    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => { expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined(); });
    expect(screen.queryByRole('navigation', { name: 'Quick Links' })).toBeNull();
    const trigger = screen.getByRole('button', { name: 'Quick Links' });
    expect(trigger).toHaveTextContent('Quick Links');
    expect(trigger).toHaveStyle({
      backgroundColor: 'rgba(18, 24, 38, 0.78)',
      color: 'rgb(255, 255, 255)',
      fontSize: '12px',
      fontWeight: '600',
      height: '48px',
      paddingLeft: '12px',
      paddingRight: '12px',
    });
  });

  it('renders Select Player Profile on mobile as a compact purple circle aligned to Quick Links', async () => {
    mockIsWideDesktop = false;
    mockHasFab = true;

    render(
      <Providers>
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={false} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
      expect(screen.getByRole('button', { name: 'Select Player Profile' })).toBeDefined();
    });

    const selectButton = screen.getByRole('button', { name: 'Select Player Profile' });
    const selectButtonStyle = selectButton.getAttribute('style') ?? '';
    expect(selectButtonStyle).toContain(`width: ${Layout.pillButtonHeight}px`);
    expect(selectButtonStyle).toContain(`height: ${Layout.pillButtonHeight}px`);
    expect(selectButtonStyle).toContain('border-radius: 999px');
    expect(selectButton.className).toContain('profileCircleBreathe');

    const selectIcon = selectButton.querySelector('svg');
    expect(selectIcon).not.toBeNull();
    expect(selectIcon).toHaveAttribute('height', `${IconSize.action}`);
    expect(selectIcon).toHaveAttribute('width', `${IconSize.action}`);

    expect(screen.getByTestId('player-select-profile-slot')).toHaveStyle({
      maxWidth: `${Layout.pillButtonHeight}px`,
      opacity: '1',
    });

    const actionButtons = Array.from(screen.getByTestId('player-header-actions').querySelectorAll('button'));
    expect(actionButtons).toHaveLength(2);
    expect(actionButtons[0]).toHaveAccessibleName('Select Player Profile');
    expect(actionButtons[1]).toHaveAccessibleName('Quick Links');
  });

  it('hides the mobile player header actions when the setting is off', async () => {
    mockIsWideDesktop = false;
    mockHasFab = true;
    localStorage.setItem('fst:appSettings', JSON.stringify({ showButtonsInHeaderMobile: false }));

    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={false} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('player-section-top-songs')).toBeDefined();
    });

    expect(screen.getByText('TestPlayer')).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Select Player Profile' })).toBeNull();
    expect(screen.queryByTestId('player-header-actions')).toBeNull();
    expect(screen.queryByTestId('player-header-actions-transition')).toBeNull();
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

    expect(screen.getByTestId('player-quick-link-bands').querySelector('svg')).not.toBeNull();
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

    expect(scrollToSpy).toHaveBeenCalledWith({ top: 640 - DEFAULT_QUICK_LINK_SCROLL_OFFSET, behavior: 'smooth' });
  });

  it('opens the quick links modal from the header trigger and closes after selection', async () => {
    mockIsWideDesktop = false;
    mockHasFab = false;

    render(
      <Providers accountId="p1">
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    });

    const shell = screen.getByTestId('page-root').parentElement as HTMLElement;
    const topSongsSection = screen.getByTestId('player-section-top-songs');
    const scrollToSpy = vi.fn();
    shell.scrollTo = scrollToSpy as any;
    shell.scrollTop = 0;
    setDynamicSectionRect(topSongsSection, 640, shell);

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    let dialog: HTMLElement;
    await waitFor(() => {
      dialog = screen.getByRole('dialog', { name: 'Quick Links' });
      expect(dialog).toBeDefined();
    });

    fireEvent.click(screen.getByTestId('player-quick-link-top-songs'));
    fireEvent.transitionEnd(dialog!);

    expect(scrollToSpy).toHaveBeenCalledWith({ top: 640 - DEFAULT_QUICK_LINK_SCROLL_OFFSET, behavior: 'smooth' });
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Quick Links' })).toBeNull();
    });
  });

  it('registers quick links for the mobile FAB context and opens the same modal', async () => {
    mockIsWideDesktop = false;
    mockHasFab = true;

    render(
      <Providers accountId="p1">
        <FabQuickLinksSpy />
        <PlayerContent data={playerData as any} songs={songs as any} isSyncing={false} phase={SyncPhase.Idle} backfillProgress={0} historyProgress={0} rivalsProgress={0} itemsCompleted={0} totalItems={0} entriesFound={0} currentSongName={null} seasonsQueried={0} rivalsFound={0} isTrackedPlayer={true} skipAnim statsData={null} rankingQueryResults={[]} />
      </Providers>,
    );

    await waitFor(() => {
      expect(screen.getByTestId('fab-player-quick-links').textContent).toBe('true');
    });

    fireEvent.click(screen.getByTestId('fab-player-quick-links-open'));

    await waitFor(() => {
      expect(screen.getByRole('dialog', { name: 'Quick Links' })).toBeDefined();
    });
  });

  it('preserves the current highlight during click-scroll and activates the clicked link only at the target', async () => {
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

    fireEvent.click(screen.getByTestId('player-quick-link-top-songs'));

    expect(screen.getByTestId('player-quick-link-global')).toHaveAttribute('aria-current', 'location');
    expect(screen.getByTestId('player-quick-link-top-songs')).not.toHaveAttribute('aria-current');

    shell.scrollTop = 600;
    fireEvent.scroll(shell);

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-global')).toHaveAttribute('aria-current', 'location');
    });
    expect(screen.getByTestId('player-quick-link-top-songs')).not.toHaveAttribute('aria-current');

    shell.scrollTop = 972;
    fireEvent.scroll(shell);

    expect(screen.getByTestId('player-quick-link-global')).toHaveAttribute('aria-current', 'location');
    expect(screen.getByTestId('player-quick-link-top-songs')).not.toHaveAttribute('aria-current');

    shell.scrollTop = 970;
    fireEvent.scroll(shell);

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-global')).toHaveAttribute('aria-current', 'location');
    });
    expect(screen.getByTestId('player-quick-link-top-songs')).not.toHaveAttribute('aria-current');

    shell.scrollTop = 972;
    fireEvent.scroll(shell);

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-top-songs')).toHaveAttribute('aria-current', 'location');
    });

    shell.scrollTop = 600;
    fireEvent.scroll(shell);

    await waitFor(() => {
      expect(screen.getByTestId('player-quick-link-instrument-solo-guitar')).toHaveAttribute('aria-current', 'location');
    });
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
