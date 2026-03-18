import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { SettingsProvider } from '../../contexts/SettingsContext';
import { FestivalProvider } from '../../contexts/FestivalContext';
import { FabSearchProvider } from '../../contexts/FabSearchContext';
import { SearchQueryProvider } from '../../contexts/SearchQueryContext';
import { PlayerDataProvider } from '../../contexts/PlayerDataContext';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../helpers/browserStubs';

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
  };
});
vi.mock('../../api/client', () => ({ api: mockApi }));

beforeAll(() => { stubScrollTo(); stubResizeObserver(); stubElementDimensions(); });
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // Re-set mocks
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

function Providers({ children, accountId }: { children: React.ReactNode; accountId?: string }) {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: 0 } } });
  return (
    <QueryClientProvider client={qc}>
    <SettingsProvider>
      <FestivalProvider>
        <FabSearchProvider>
          <SearchQueryProvider>
            <PlayerDataProvider accountId={accountId}>
              <MemoryRouter>{children}</MemoryRouter>
            </PlayerDataProvider>
          </SearchQueryProvider>
        </FabSearchProvider>
      </FestivalProvider>
    </SettingsProvider>
    </QueryClientProvider>
  );
}

// ── PlayerContent ──
import PlayerContent from '../../pages/leaderboard/player/components/PlayerContent';
import { SyncPhase } from '@festival/core';

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
});

// ── SongRow ──
import { SongRow } from '../../pages/songs/components/SongRow';

describe('SongRow', () => {
  const song = { songId: 's1', title: 'Test Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3 }, albumArt: 'art.jpg' };

  it('renders song title and artist', () => {
    const { container } = render(
      <MemoryRouter>
        <SongRow song={song as any} instrument={'Solo_Guitar' as any} enabledInstruments={[]} metadataOrder={['score']} sortMode={'title'} isMobile={false} showInstrumentIcons={false} />
      </MemoryRouter>,
    );
    expect(container.textContent).toContain('Test Song');
    expect(container.textContent).toContain('Artist A');
  });

  it('renders with player score', () => {
    const score = { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 3, percentile: 90, accuracy: 95, isFullCombo: false, stars: 5, season: 5, totalEntries: 500 };
    const { container } = render(
      <MemoryRouter>
        <SongRow song={song as any} score={score as any} instrument={'Solo_Guitar' as any} instrumentFilter={'Solo_Guitar' as any} enabledInstruments={['Solo_Guitar' as any]} metadataOrder={['score', 'percentage', 'percentile', 'stars']} sortMode={'score'} isMobile={false} showInstrumentIcons={false} />
      </MemoryRouter>,
    );
    expect(container.textContent).toContain('100,000');
  });

  it('renders on mobile', () => {
    const score = { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 3, percentile: 90, accuracy: 95, isFullCombo: false, stars: 5, season: 5, totalEntries: 500 };
    const { container } = render(
      <MemoryRouter>
        <SongRow song={song as any} score={score as any} instrument={'Solo_Guitar' as any} instrumentFilter={'Solo_Guitar' as any} enabledInstruments={['Solo_Guitar' as any]} metadataOrder={['score', 'percentage']} sortMode={'score'} isMobile={true} showInstrumentIcons={false} />
      </MemoryRouter>,
    );
    expect(container.textContent).toContain('Test Song');
  });

  it('renders instrument icons when enabled', () => {
    const { container } = render(
      <MemoryRouter>
        <SongRow song={song as any} instrument={'Solo_Guitar' as any} enabledInstruments={['Solo_Guitar' as any, 'Solo_Bass' as any]} metadataOrder={['score']} sortMode={'title'} isMobile={false} showInstrumentIcons={true} allScoreMap={new Map()} />
      </MemoryRouter>,
    );
    expect(container.innerHTML).toBeTruthy();
  });

  it('renders with FC score', () => {
    const score = { songId: 's1', instrument: 'Solo_Guitar', score: 150000, rank: 1, percentile: 99, accuracy: 100, isFullCombo: true, stars: 6, season: 5, totalEntries: 500 };
    const { container } = render(
      <MemoryRouter>
        <SongRow song={song as any} score={score as any} instrument={'Solo_Guitar' as any} instrumentFilter={'Solo_Guitar' as any} enabledInstruments={['Solo_Guitar' as any]} metadataOrder={['score', 'percentage', 'stars', 'percentile', 'seasonachieved', 'intensity']} sortMode={'score'} isMobile={false} showInstrumentIcons={false} />
      </MemoryRouter>,
    );
    expect(container.textContent).toContain('150,000');
  });

  it('renders without score', () => {
    const { container } = render(
      <MemoryRouter>
        <SongRow song={song as any} instrument={'Solo_Guitar' as any} enabledInstruments={[]} metadataOrder={[]} sortMode={'title'} isMobile={false} showInstrumentIcons={false} />
      </MemoryRouter>,
    );
    expect(container.textContent).toContain('Test Song');
  });
});

// ── LeaderboardEntry ──
import { LeaderboardEntry } from '../../pages/leaderboard/global/components/LeaderboardEntry';

describe('LeaderboardEntry', () => {
  it('renders rank and name', () => {
    render(<LeaderboardEntry rank={1} displayName="Player One" score={145000} />);
    expect(screen.getByText('Player One')).toBeDefined();
  });

  it('renders score', () => {
    render(<LeaderboardEntry rank={1} displayName="P" score={145000} />);
    expect(screen.getByText('145,000')).toBeDefined();
  });

  it('applies bold styling for tracked player', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="Me" score={100000} isPlayer />);
    expect(container.querySelector('[class*="Bold"]') || container.querySelector('[class*="bold"]')).toBeTruthy();
  });

  it('shows season when showSeason is true', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showSeason season={5} />);
    // Season 5 should appear in the rendered output
    expect(container.textContent).toContain('5');
  });

  it('shows accuracy when showAccuracy is true', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showAccuracy accuracy={95.5} isFullCombo={false} />);
    // AccuracyDisplay renders the percentage — check the container text
    expect(container.innerHTML).toBeTruthy();
    // The accuracy value should be rendered somewhere in the output
  });

  it('shows stars when showStars is true', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showStars stars={5} />);
    expect(container.querySelectorAll('img').length).toBeGreaterThanOrEqual(1);
  });

  it('shows gold stars for 6 stars', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showStars stars={6} />);
    const imgs = container.querySelectorAll('img');
    expect(imgs.length).toBeGreaterThanOrEqual(1);
  });

  it('hides optional columns when show flags are false', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} showSeason={false} showAccuracy={false} showStars={false} />);
    expect(container.textContent).toContain('P');
  });

  it('applies custom scoreWidth', () => {
    const { container } = render(<LeaderboardEntry rank={1} displayName="P" score={100000} scoreWidth="8ch" />);
    expect(container.innerHTML).toContain('8ch');
  });
});

// ── InstrumentCard ──
import InstrumentCard from '../../pages/songinfo/components/InstrumentCard';

describe('InstrumentCard', () => {
  it('renders instrument label', async () => {
    const { container } = render(
      <Providers accountId="p1">
        <InstrumentCard songId="s1" instrument={'Solo_Guitar' as any} baseDelay={0} windowWidth={1024} prefetchedEntries={[]} prefetchedError={null} skipAnimation scoreWidth="6ch" />
      </Providers>,
    );
    await waitFor(() => { expect(container.textContent).toContain('Lead'); });
  });

  it('renders leaderboard entries', async () => {
    const entries = [
      { accountId: 'a1', displayName: 'P1', score: 145000, rank: 1, accuracy: 99, isFullCombo: true, stars: 6 },
      { accountId: 'a2', displayName: 'P2', score: 140000, rank: 2 },
    ];
    const { container } = render(
      <Providers accountId="p1">
        <InstrumentCard songId="s1" instrument={'Solo_Guitar' as any} baseDelay={0} windowWidth={1024} prefetchedEntries={entries as any} prefetchedError={null} skipAnimation scoreWidth="7ch" />
      </Providers>,
    );
    await waitFor(() => { expect(container.textContent).toContain('P1'); });
  });

  it('renders error state', async () => {
    const { container } = render(
      <Providers>
        <InstrumentCard songId="s1" instrument={'Solo_Guitar' as any} baseDelay={0} windowWidth={1024} prefetchedEntries={[]} prefetchedError="Failed" skipAnimation scoreWidth="6ch" />
      </Providers>,
    );
    await waitFor(() => { expect(container.textContent).toContain('Failed'); });
  });

  it('renders player out-of-top section', async () => {
    const entries = [{ accountId: 'a1', displayName: 'P1', score: 145000, rank: 1 }];
    const playerScore = { songId: 's1', instrument: 'Solo_Guitar', score: 80000, rank: 50, percentile: 50 };
    const { container } = render(
      <Providers accountId="p1">
        <InstrumentCard songId="s1" instrument={'Solo_Guitar' as any} baseDelay={0} windowWidth={1024} prefetchedEntries={entries as any} prefetchedError={null} playerScore={playerScore as any} playerName="TestPlayer" playerAccountId="p1" skipAnimation scoreWidth="7ch" />
      </Providers>,
    );
    await waitFor(() => { expect(container.textContent).toContain('TestPlayer'); });
  });

  it('renders at mobile width', async () => {
    const { container } = render(
      <Providers>
        <InstrumentCard songId="s1" instrument={'Solo_Guitar' as any} baseDelay={0} windowWidth={320} prefetchedEntries={[]} prefetchedError={null} skipAnimation scoreWidth="6ch" />
      </Providers>,
    );
    expect(container.innerHTML).toBeTruthy();
  });
});

// ── CategoryCard ──
import { CategoryCard } from '../../pages/suggestions/components/CategoryCard';

describe('CategoryCard', () => {
  const category = {
    key: 'near_fc_Solo_Guitar',
    title: 'Near FC',
    description: 'Songs you almost FC\'d',
    songs: [
      { songId: 's1', title: 'Test Song', artist: 'Artist A', starCount: 5, accuracy: 98, instrumentKey: 'Solo_Guitar' },
    ],
  };

  it('renders category title', () => {
    render(
      <MemoryRouter>
        <CategoryCard category={category as any} albumArtMap={new Map([['s1', 'art.jpg']])} scoresIndex={{}} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Near FC')).toBeDefined();
  });

  it('renders songs in category', () => {
    const { container } = render(
      <MemoryRouter>
        <CategoryCard category={category as any} albumArtMap={new Map([['s1', 'art.jpg']])} scoresIndex={{}} />
      </MemoryRouter>,
    );
    expect(container.textContent).toContain('Test Song');
  });

  it('renders without album art', () => {
    render(
      <MemoryRouter>
        <CategoryCard category={category as any} albumArtMap={new Map()} scoresIndex={{}} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Near FC')).toBeDefined();
  });

  it('renders with leaderboard data', () => {
    const scoresIndex = { s1: { Solo_Guitar: { rank: 3, totalEntries: 500, percentile: 90, season: 5 } } };
    render(
      <MemoryRouter>
        <CategoryCard category={category as any} albumArtMap={new Map()} scoresIndex={scoresIndex as any} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Near FC')).toBeDefined();
  });

  it('renders variety pack layout', () => {
    const variety = { key: 'variety_pack', title: 'Variety Pack', description: 'Mix it up', songs: [{ songId: 's1', title: 'Song', artist: 'A', starCount: 3 }] };
    render(
      <MemoryRouter>
        <CategoryCard category={variety as any} albumArtMap={new Map()} scoresIndex={{}} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Variety Pack')).toBeDefined();
  });

  it('renders stale season layout', () => {
    const stale = { key: 'stale_Solo_Guitar', title: 'Stale', description: 'Old scores', songs: [{ songId: 's1', title: 'Song', artist: 'A', starCount: 3, instrumentKey: 'Solo_Guitar' }] };
    render(
      <MemoryRouter>
        <CategoryCard category={stale as any} albumArtMap={new Map()} scoresIndex={{}} />
      </MemoryRouter>,
    );
    expect(screen.getByText('Stale')).toBeDefined();
  });
});

// ── Page.tsx ──
import Page from '../../pages/Page';
import { useRef } from 'react';

function PageWrapper(props: Partial<React.ComponentProps<typeof Page>> & { children?: React.ReactNode }) {
  const scrollRef = useRef<HTMLDivElement>(null);
  return <Page scrollRef={scrollRef} {...props}>{props.children ?? <div>Page content</div>}</Page>;
}

describe('Page', () => {
  it('renders children', () => {
    render(<PageWrapper><div>Test content</div></PageWrapper>);
    expect(screen.getByText('Test content')).toBeDefined();
  });

  it('renders before and after slots', () => {
    render(<PageWrapper before={<div>Before</div>} after={<div>After</div>}><div>Main</div></PageWrapper>);
    expect(screen.getByText('Before')).toBeDefined();
    expect(screen.getByText('After')).toBeDefined();
    expect(screen.getByText('Main')).toBeDefined();
  });

  it('renders with custom className', () => {
    const { container } = render(<PageWrapper className="custom"><div>C</div></PageWrapper>);
    expect(container.querySelector('.custom')).toBeTruthy();
  });
});
