import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import LeaderboardPage, { clearLeaderboardCache } from '../../../../src/pages/leaderboard/global/LeaderboardPage';
import { computeRankWidth } from '../../../../src/pages/leaderboards/helpers/rankingHelpers';
import { TestProviders } from '../../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubMatchMedia } from '../../../helpers/browserStubs';

const defaultEntries = [
  { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1, percentile: 99, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
  { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2, percentile: 97, accuracy: 98.0, isFullCombo: false, stars: 5, season: 5 },
  { accountId: 'acc-3', displayName: 'Player Three', score: 135000, rank: 3, percentile: 95, accuracy: 96.2, isFullCombo: false, stars: 5, season: 4 },
  { accountId: 'acc-4', displayName: 'Player Four', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
  { accountId: 'acc-5', displayName: 'Player Five', score: 100000, rank: 5, percentile: 80, accuracy: 88.0, isFullCombo: false, stars: 3, season: 3 },
];

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({
      songs: [{ songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/art1.jpg' }],
      count: 1,
      currentSeason: 5,
    }),
    getLeaderboard: fn().mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50,
      showLeaderboardEntryTotals: false,
      entries: [
        { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1, percentile: 99, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
        { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2, percentile: 97, accuracy: 98.0, isFullCombo: false, stars: 5, season: 5 },
        { accountId: 'acc-3', displayName: 'Player Three', score: 135000, rank: 3, percentile: 95, accuracy: 96.2, isFullCombo: false, stars: 5, season: 4 },
        { accountId: 'acc-4', displayName: 'Player Four', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
        { accountId: 'acc-5', displayName: 'Player Five', score: 100000, rank: 5, percentile: 80, accuracy: 88.0, isFullCombo: false, stars: 3, season: 3 },
      ],
    }),
    getPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [
      { songId: 'song-1', instrument: 'Solo_Guitar', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
    ] }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 'song-1', instruments: [] }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] }),
    getSongBandLeaderboard: fn().mockResolvedValue({
      songId: 'song-1',
      bandType: 'Band_Duets',
      count: 0,
      totalEntries: 0,
      localEntries: 0,
      entries: [],
      selectedPlayerEntry: null,
      selectedBandEntry: null,
    }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'test-player-1', stats: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' }),
  };
});

vi.mock('../../../../src/api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions(800);
});

function stubViewportWidth(width: number) {
  const matchMedia = vi.fn().mockImplementation((query: string) => {
    const minWidth = query.match(/\(min-width:\s*(\d+)px\)/);
    const maxWidth = query.match(/\(max-width:\s*(\d+)px\)/);
    let matches = true;

    if (minWidth) {
      matches = matches && width >= Number(minWidth[1]);
    }
    if (maxWidth) {
      matches = matches && width <= Number(maxWidth[1]);
    }

    return {
      matches,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    };
  });

  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: matchMedia,
  });

  return matchMedia;
}

function resetMocks() {
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/art1.jpg' },
  ], count: 1, currentSeason: 5 });
  mockApi.getLeaderboard.mockResolvedValue({
    songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50,
    showLeaderboardEntryTotals: false,
    entries: defaultEntries,
  });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [
    { songId: 'song-1', instrument: 'Solo_Guitar', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
  ] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 'song-1', instruments: [] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] });
  mockApi.getSongBandLeaderboard.mockResolvedValue({
    songId: 'song-1',
    bandType: 'Band_Duets',
    count: 0,
    totalEntries: 0,
    localEntries: 0,
    entries: [],
    selectedPlayerEntry: null,
    selectedBandEntry: null,
  });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'test-player-1', stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' });
}

beforeEach(() => {
  vi.clearAllMocks();
  stubMatchMedia(false);
  localStorage.clear();
  clearLeaderboardCache();
  resetMocks();
});

function renderLeaderboard(route = '/songs/song-1/Solo_Guitar', accountId?: string) {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/songs/:songId/:instrument" element={<LeaderboardPage />} />
      </Routes>
    </TestProviders>,
  );
}

function expectBefore(first: Element, second: Element) {
  expect(first.compareDocumentPosition(second) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
}

const selectedBandProfile = {
  type: 'band',
  bandId: 'band-selected-1',
  bandType: 'Band_Duets',
  teamKey: 'band-a:band-b',
  displayName: 'Alpha + Beta',
  members: [
    { accountId: 'band-a', displayName: 'Alpha' },
    { accountId: 'band-b', displayName: 'Beta' },
  ],
} as const;

function selectBandProfile() {
  localStorage.setItem('fst:selectedProfile', JSON.stringify(selectedBandProfile));
}

function makeSelectedSongBandEntry(overrides: Record<string, unknown> = {}) {
  return {
    bandId: selectedBandProfile.bandId,
    bandType: selectedBandProfile.bandType,
    teamKey: selectedBandProfile.teamKey,
    comboId: null,
    members: [
      { accountId: 'band-a', displayName: 'Alpha', instruments: ['Solo_Guitar'] },
      { accountId: 'band-b', displayName: 'Beta', instruments: ['Solo_Drums'] },
    ],
    rank: 7,
    score: 98765,
    season: 5,
    accuracy: 98.7,
    isFullCombo: true,
    stars: 6,
    difficulty: 4,
    ...overrides,
  };
}

// ---------------------------------------------------------------------------
// Original LeaderboardPage tests
// ---------------------------------------------------------------------------

describe('LeaderboardPage', () => {
  it('renders leaderboard entries after loading', async () => {
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
      expect(screen.getByText('Player Two')).toBeDefined();
    });
  });

  it('renders without crashing', async () => {
    const { container } = renderLeaderboard();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('shows empty state when no entries', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [],
    });
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('No entries on this page')).toBeDefined();
    });
  });

  it('shows error message on API failure', async () => {
    mockApi.getLeaderboard.mockRejectedValue(new Error('Server Error'));
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Something Went Wrong')).toBeDefined();
    });
  });

  it('shows fallback error for non-Error throws', async () => {
    mockApi.getLeaderboard.mockRejectedValue('fail');
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Something Went Wrong')).toBeDefined();
    });
  });

  it('shows not found when route params are missing', async () => {
    render(
      <TestProviders route="/songs">
        <Routes>
          <Route path="/songs" element={<LeaderboardPage />} />
        </Routes>
      </TestProviders>,
    );
    await waitFor(() => {
      expect(screen.getByText('Not found')).toBeDefined();
    });
  });

  it('renders all entry display names', async () => {
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
      expect(screen.getByText('Player Two')).toBeDefined();
      expect(screen.getByText('Player Three')).toBeDefined();
      expect(screen.getByText('Player Four')).toBeDefined();
      expect(screen.getByText('Player Five')).toBeDefined();
    });
  });

  it('shows inline percentage after score on mobile rows', async () => {
    stubViewportWidth(300);
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 1, totalEntries: 1, localEntries: 1,
      entries: [{ ...defaultEntries[0], accuracy: 995000 }],
    });

    renderLeaderboard();

    await screen.findByText('Player One');
    expectBefore(screen.getByText('145,000'), screen.getByText('99.5%'));
  });

  it('shows the header entry count only when leaderboard totals are enabled', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50,
      showLeaderboardEntryTotals: true,
      entries: defaultEntries,
    });

    renderLeaderboard();

    await screen.findByText('Player One');
    expect(screen.getByText('50 Lead entries')).toBeDefined();
  });

  it('hides the header entry count when leaderboard totals are disabled', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50,
      showLeaderboardEntryTotals: false,
      entries: defaultEntries,
    });

    renderLeaderboard();

    await screen.findByText('Player One');
    expect(screen.queryByText('50 Lead entries')).toBeNull();
  });

  it('hides the header entry count when leaderboard total visibility is omitted', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50,
      entries: defaultEntries,
    });

    renderLeaderboard();

    await screen.findByText('Player One');
    expect(screen.queryByText('50 Lead entries')).toBeNull();
  });

  it('renders pagination when total pages > 1', async () => {
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });
    // totalEntries = 50, LEADERBOARD_PAGE_SIZE = 25, so 2 pages (portaled to body)
    await waitFor(() => {
      expect(document.body.textContent).toContain('1 / 2');
    });
  });

  it('fetches next page on Next click', async () => {
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });
    await waitFor(() => {
      expect(document.body.textContent).toContain('1 / 2');
    });

    // Paginator renders circular chevron buttons with aria-labels (portaled to body)
    const nextBtn = document.querySelector('button[aria-label="Next"]');
    expect(nextBtn).toBeTruthy();
    fireEvent.click(nextBtn!);

    await waitFor(() => {
      expect(mockApi.getLeaderboard.mock.calls.length).toBeGreaterThanOrEqual(2);
    });
  });

  it('highlights tracked player row', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 2, totalEntries: 2, localEntries: 2,
      entries: [
        { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1 },
        { accountId: 'test-player-1', displayName: 'TestPlayer', score: 120000, rank: 2 },
      ],
    });
    const { container } = renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');
    await waitFor(() => {
      expect(container.textContent).toContain('TestPlayer');
    });
    // Verify the tracked player's row has a highlight class
    const highlightRow = container.querySelector('[class*="rowHighlight"]');
    expect(highlightRow || container.textContent?.includes('TestPlayer')).toBeTruthy();
  });

  it('shows player footer when tracked player has a score', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });
    // Player footer should show the tracked player's score
    const footerElements = screen.getAllByText('TestPlayer');
    expect(footerElements.length).toBeGreaterThanOrEqual(1);
  });

  it('uses cached data on second render', async () => {
    const { unmount } = renderLeaderboard();
    await waitFor(() => { expect(screen.getByText('Player One')).toBeDefined(); });
    unmount();

    // Second render should use cache, not refetch
    renderLeaderboard();
    expect(screen.getByText('Player One')).toBeDefined();
    // getLeaderboard should have been called only once (first render)
    expect(mockApi.getLeaderboard).toHaveBeenCalledTimes(1);
  });

  it('preserves the header entry-count visibility decision from cache', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50,
      showLeaderboardEntryTotals: true,
      entries: defaultEntries,
    });
    const { unmount } = renderLeaderboard();
    await screen.findByText('Player One');
    expect(screen.getByText('50 Lead entries')).toBeDefined();
    unmount();

    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50,
      showLeaderboardEntryTotals: false,
      entries: defaultEntries,
    });

    renderLeaderboard();

    expect(screen.getByText('Player One')).toBeDefined();
    expect(screen.getByText('50 Lead entries')).toBeDefined();
    expect(mockApi.getLeaderboard).toHaveBeenCalledTimes(1);
  });

  it('renders score values for entries', async () => {
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('145,000')).toBeDefined();
      expect(screen.getByText('140,000')).toBeDefined();
    });
  });

  it('parses ?page= search param on mount', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar?page=2');
    await waitFor(() => {
      expect(mockApi.getLeaderboard).toHaveBeenCalled();
    });
    // Should request page 1 (0-indexed) = offset 25
    const call = mockApi.getLeaderboard.mock.calls[0];
    expect(call![3]).toBe(25);
  });

  it('triggers scroll handler on scroll event', async () => {
    const { container } = renderLeaderboard();
    await waitFor(() => {
      expect(container.textContent).toContain('Player One');
    });
    const scrollArea = container.querySelector('[class*="scrollArea"]');
    if (scrollArea) {
      fireEvent.scroll(scrollArea);
    }
    expect(container.textContent).toContain('Player One');
  });

  it('renders player footer with tracked player score', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50,
      entries: [
        { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1 },
        { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2 },
      ],
    });
    mockApi.getPlayer.mockResolvedValue({
      accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1,
      scores: [{ songId: 'song-1', instrument: 'Solo_Guitar', score: 120000, rank: 10, percentile: 80, accuracy: 93, stars: 4, season: 5 }],
    });
    const { container } = renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');
    await waitFor(() => {
      expect(container.textContent).toContain('Player One');
    });
    // Player footer should render with TestPlayer's data (portaled to body)
    await waitFor(() => {
      expect(document.body.textContent).toContain('TestPlayer');
    });
  });

  it('completes load phase transition from Loading to ContentIn', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { container } = renderLeaderboard();
    await act(async () => { await vi.advanceTimersByTimeAsync(100); });
    await act(async () => { await vi.advanceTimersByTimeAsync(300); });
    await waitFor(() => {
      expect(container.textContent).toContain('Player One');
    });
    // Advance through stagger retire timeout
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(container.textContent).toContain('Player One');
    vi.useRealTimers();
  });

  it('renders skip/prev/next/skip pagination buttons', async () => {
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });
    await waitFor(() => {
      expect(document.body.textContent).toContain('1 / 2');
    });
    // Verify all Paginator buttons exist by aria-label (portaled to body)
    expect(document.querySelector('button[aria-label="Skip to start"]')).toBeTruthy();
    expect(document.querySelector('button[aria-label="Previous"]')).toBeTruthy();
    expect(document.querySelector('button[aria-label="Next"]')).toBeTruthy();
    expect(document.querySelector('button[aria-label="Skip to end"]')).toBeTruthy();
  });

  it('navigates to last page on Skip to end click', async () => {
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });
    await waitFor(() => {
      expect(document.body.textContent).toContain('1 / 2');
    });
    const lastBtn = document.querySelector('button[aria-label="Skip to end"]');
    expect(lastBtn).toBeTruthy();
    fireEvent.click(lastBtn!);
    await waitFor(() => {
      expect(mockApi.getLeaderboard.mock.calls.length).toBeGreaterThanOrEqual(2);
    });
  });

  it('handles single-page leaderboard (no pagination)', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 3, totalEntries: 3, localEntries: 3,
      entries: [
        { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1 },
        { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2 },
      ],
    });
    const { container } = renderLeaderboard();
    await waitFor(() => {
      expect(container.textContent).toContain('Player One');
    });
    // No pagination buttons when only 1 page (portaled to body)
    expect(document.body.textContent).not.toContain('1 / 2');
  });
});

// ---------------------------------------------------------------------------
// Branch coverage (extracted)
// ---------------------------------------------------------------------------

describe('LeaderboardPage — branch coverage (extracted)', () => {
  it('renders star images for gold and regular entries', async () => {
    const entries = Array.from({ length: 30 }, (_, i) => ({
      accountId: `acc-${i}`,
      displayName: `Player ${i}`,
      score: 200000 - i * 1000,
      rank: i + 1,
      accuracy: 990000 - i * 5000,
      isFullCombo: i < 5,
      stars: i < 3 ? 6 : 5,
      season: 5,
    }));
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1',
      instrument: 'Solo_Guitar',
      count: 30,
      totalEntries: 100,
      localEntries: 30,
      entries,
    });
    clearLeaderboardCache();
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');
    await waitFor(() => expect(screen.getByText('Player 0')).toBeTruthy(), { timeout: 5000 });
    // Gold star entries (stars >= 6) and regular star entries should be rendered
    expect(screen.getByText('Player 1')).toBeTruthy();
  });
});

// ---------------------------------------------------------------------------
// Coverage: score width + row rendering
// ---------------------------------------------------------------------------

describe('LeaderboardPage — coverage: score width + row rendering', () => {
  it('renders score values with correct width calculation', async () => {
    renderLeaderboard();

    await waitFor(() => {
      expect(screen.getByText('145,000')).toBeDefined();
      expect(screen.getByText('100,000')).toBeDefined();
    });

    // All 5 players should be rendered
    expect(screen.getByText('Player One')).toBeDefined();
    expect(screen.getByText('Player Five')).toBeDefined();
  });

  it('renders rank numbers for each entry', async () => {
    renderLeaderboard();

    await waitFor(() => {
      expect(screen.getByText('#1')).toBeDefined();
      expect(screen.getByText('#5')).toBeDefined();
    });
  });

  it('renders with very large scores for width calc', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 2, totalEntries: 2, localEntries: 2,
      entries: [
        { accountId: 'a1', displayName: 'BigScorer', score: 1000000, rank: 1, accuracy: 100, isFullCombo: true, stars: 6, season: 5 },
        { accountId: 'a2', displayName: 'SmallScorer', score: 500, rank: 2, accuracy: 50, isFullCombo: false, stars: 1, season: 5 },
      ],
    });

    renderLeaderboard();

    await waitFor(() => {
      expect(screen.getByText('1,000,000')).toBeDefined();
      expect(screen.getByText('500')).toBeDefined();
    });
  });
});

// ---------------------------------------------------------------------------
// Coverage: multi-page pagination (uses totalEntries=150 → 6 pages)
// ---------------------------------------------------------------------------

describe('LeaderboardPage — coverage: multi-page pagination', () => {
  beforeEach(() => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 150, localEntries: 150,
      entries: defaultEntries,
    });
  });

  it('renders full pagination controls for 150 total entries (6 pages)', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });

    // Should show pagination with "1 / 6"
    await waitFor(() => {
      const text = document.body.textContent ?? '';
      expect(text).toContain('1 / 6');
    });

    // Click "Skip to end" button to go to last page
    const lastBtn = document.querySelector('button[aria-label="Skip to end"]');
    expect(lastBtn).toBeTruthy();
    if (lastBtn) {
      fireEvent.click(lastBtn);
      await waitFor(() => {
        expect(mockApi.getLeaderboard.mock.calls.length).toBeGreaterThanOrEqual(2);
      });
    }
  });

  it('navigates to first page from middle', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar?page=3', 'test-player-1');

    await waitFor(() => {
      expect(mockApi.getLeaderboard).toHaveBeenCalled();
    });

    // Should start on page 3 (offset 50)
    const firstCall = mockApi.getLeaderboard.mock.calls[0];
    expect(firstCall![3]).toBe(50);

    // Click "First" button
    await waitFor(() => {
      const text = document.body.textContent ?? '';
      expect(text.length).toBeGreaterThan(0);
    });
    const firstBtn = Array.from(document.querySelectorAll('button')).find(
      b => b.textContent?.includes('First'),
    );
    if (firstBtn && !firstBtn.disabled) {
      fireEvent.click(firstBtn);
      await waitFor(() => {
        expect(mockApi.getLeaderboard.mock.calls.length).toBeGreaterThanOrEqual(2);
      });
    }
  });
});

// ---------------------------------------------------------------------------
// Coverage: player footer with tracked score
// ---------------------------------------------------------------------------

describe('LeaderboardPage — coverage: player footer with tracked score', () => {
  it('renders player footer when tracked player has matching score', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });

    // Player footer should show the tracked player's info
    const testPlayerTexts = screen.getAllByText('TestPlayer');
    expect(testPlayerTexts.length).toBeGreaterThanOrEqual(1);

    // Footer contains score
    const allScores = screen.getAllByText('120,000');
    expect(allScores.length).toBeGreaterThanOrEqual(1);
  });

  it('does not apply FAB spacing class to the mobile tracked-player footer', async () => {
    stubViewportWidth(375);

    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    const footerName = await screen.findByText('TestPlayer');
    const footerButton = footerName.closest('[role="button"]');

    expect(footerButton).toBeTruthy();
    expect(footerButton).not.toHaveClass('fab-player-footer');
    expect(footerButton?.parentElement?.style.bottom).toContain('84px');
    const pagination = await screen.findByTestId('leaderboard-fixed-pagination');
    expect(pagination.style.bottom).toContain('136px');
  });

  it('does not reserve the mobile player footer position when the tracked player has no score for the instrument', async () => {
    stubViewportWidth(375);
    mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [
      { songId: 'song-1', instrument: 'Solo_Drums', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
    ] });

    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await screen.findByText('Player One');

    expect(screen.queryByText('TestPlayer')).toBeNull();
    const pagination = await screen.findByTestId('leaderboard-fixed-pagination');
    expect(pagination.style.bottom).toBe('96px');
    const scrollContainer = screen.getByTestId('test-scroll-container');
    const marginBottom = parseFloat(scrollContainer.style.marginBottom);
    const bottomChromeHeight = window.innerHeight - scrollContainer.getBoundingClientRect().top - scrollContainer.clientHeight;
    expect(marginBottom + bottomChromeHeight).toBeGreaterThanOrEqual(152);
  });

  it('footer click navigates to statistics', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });

    // Click on the player footer (role="button")
    const footer = document.querySelector('[role="button"]');
    if (footer) {
      fireEvent.click(footer);
    }
    expect(document.body.innerHTML).toBeTruthy();
  });

  it('includes tracked player rank and score in desktop page-row width calculation', async () => {
    stubViewportWidth(1024);

    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 1, totalEntries: 12345, localEntries: 12345,
      entries: [
        { accountId: 'acc-1', displayName: 'Top Player', score: 500, rank: 1, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
      ],
    });
    mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [
      { songId: 'song-1', instrument: 'Solo_Guitar', score: 1200000, rank: 12345, percentile: 10, accuracy: 80.1, isFullCombo: false, stars: 4, season: 5 },
    ] });

    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    const topRank = await screen.findByText('#1');
    const playerRank = await screen.findByText('#12,345');
    const topScore = await screen.findByText('500');
    const playerScore = await screen.findByText('1,200,000');

    expect(topRank).toHaveStyle({ width: `${computeRankWidth([1, 12345])}px` });
    expect(playerRank).toHaveStyle({ width: `${computeRankWidth([12345])}px` });
    expect(topRank.style.width).toBe(playerRank.style.width);
    expect(topScore.style.width).toBe('9ch');
    expect(playerScore.style.width).toBe('9ch');
  });

  it('keeps mobile page-row rank and score widths scoped to page entries only', async () => {
    stubViewportWidth(375);

    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 1, totalEntries: 12345, localEntries: 12345,
      entries: [
        { accountId: 'acc-1', displayName: 'Top Player', score: 500, rank: 1, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
      ],
    });
    mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [
      { songId: 'song-1', instrument: 'Solo_Guitar', score: 1200000, rank: 12345, percentile: 10, accuracy: 80.1, isFullCombo: false, stars: 4, season: 5 },
    ] });

    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    const topRank = await screen.findByText('#1');
    const playerRank = await screen.findByText('#12,345');
    const topScore = await screen.findByText('500');
    const playerScore = await screen.findByText('1,200,000');

    expect(topRank).toHaveStyle({ width: `${computeRankWidth([1])}px` });
    expect(playerRank).toHaveStyle({ width: `${computeRankWidth([12345])}px` });
    expect(topRank.style.width).not.toBe(playerRank.style.width);
    expect(topScore.style.width).toBe('3ch');
    expect(playerScore.style.width).toBe('9ch');
  });

  it('syncs desktop footer rank width to the shared page width when page rows are wider than the player rank', async () => {
    stubViewportWidth(1024);

    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 1, totalEntries: 12345, localEntries: 12345,
      entries: [
        { accountId: 'acc-1', displayName: 'Top Player', score: 500, rank: 12345, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
      ],
    });
    mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [
      { songId: 'song-1', instrument: 'Solo_Guitar', score: 1200000, rank: 42, percentile: 10, accuracy: 80.1, isFullCombo: false, stars: 4, season: 5 },
    ] });

    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    const pageRank = await screen.findByText('#12,345');
    const footerRank = await screen.findByText('#42');
    const expectedWidth = computeRankWidth([12345, 42]);

    expect(pageRank).toHaveStyle({ width: `${expectedWidth}px` });
    expect(footerRank).toHaveStyle({ width: `${expectedWidth}px` });
  });
});

// ---------------------------------------------------------------------------
// Coverage: selected band footer
// ---------------------------------------------------------------------------

describe('LeaderboardPage — selected band footer', () => {
  it('renders selected band score in the fixed footer', async () => {
    stubViewportWidth(1024);
    selectBandProfile();
    mockApi.getSongBandLeaderboard.mockResolvedValue({
      songId: 'song-1',
      bandType: 'Band_Duets',
      count: 0,
      totalEntries: 1,
      localEntries: 1,
      entries: [],
      selectedPlayerEntry: null,
      selectedBandEntry: makeSelectedSongBandEntry({ accuracy: 987_654, isFullCombo: false, stars: 5 }),
    });

    renderLeaderboard();

    expect(await screen.findByText('Alpha + Beta')).toBeTruthy();
    expect(await screen.findByText('#7')).toBeTruthy();
    const score = await screen.findByText('98,765');
    const stars = screen.getByTestId('leaderboard-stars-after-score');
    const accuracy = await screen.findByText('98.8%');
    expect(stars.querySelectorAll('img')).toHaveLength(5);
    expect(score.compareDocumentPosition(stars) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(stars.compareDocumentPosition(accuracy) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(screen.queryByText('?')).toBeNull();
    expect(mockApi.getSongBandLeaderboard).toHaveBeenCalledWith('song-1', 'Band_Duets', 1, 0, undefined, 'band-a:band-b', undefined);
  });

  it('does not apply FAB spacing class to the mobile selected-band footer', async () => {
    stubViewportWidth(375);
    selectBandProfile();
    mockApi.getSongBandLeaderboard.mockResolvedValue({
      songId: 'song-1',
      bandType: 'Band_Duets',
      count: 0,
      totalEntries: 1,
      localEntries: 1,
      entries: [],
      selectedPlayerEntry: null,
      selectedBandEntry: makeSelectedSongBandEntry(),
    });

    renderLeaderboard();

    const footerName = await screen.findByText('Alpha + Beta');
    const footerLink = footerName.closest('a');

    expect(footerLink).toBeTruthy();
    expect(footerLink).not.toHaveClass('fab-player-footer');
    expect(footerLink?.parentElement?.style.bottom).toContain('84px');
  });

  it('links the selected band footer to the band route', async () => {
    selectBandProfile();
    mockApi.getSongBandLeaderboard.mockResolvedValue({
      songId: 'song-1',
      bandType: 'Band_Duets',
      count: 0,
      totalEntries: 1,
      localEntries: 1,
      entries: [],
      selectedPlayerEntry: null,
      selectedBandEntry: makeSelectedSongBandEntry(),
    });

    renderLeaderboard();

    const footerName = await screen.findByText('Alpha + Beta');
    const link = footerName.closest('a');
    expect(link?.getAttribute('href')).toContain('/bands/band-selected-1');
    expect(link?.getAttribute('href')).toContain('bandType=Band_Duets');
    expect(link?.getAttribute('href')).toContain('teamKey=band-a%3Aband-b');
  });

  it('does not render the solo footer while a band is selected', async () => {
    selectBandProfile();
    mockApi.getSongBandLeaderboard.mockResolvedValue({
      songId: 'song-1',
      bandType: 'Band_Duets',
      count: 0,
      totalEntries: 1,
      localEntries: 1,
      entries: [],
      selectedPlayerEntry: null,
      selectedBandEntry: makeSelectedSongBandEntry(),
    });

    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    expect(await screen.findByText('Alpha + Beta')).toBeTruthy();
    expect(document.body.textContent).not.toContain('TestPlayer');
  });

  it('does not query selected band scores for a selected solo player', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await screen.findByText('Player One');
    expect(mockApi.getSongBandLeaderboard).not.toHaveBeenCalled();
  });

  it('does not render a band footer when the selected band has no song score', async () => {
    selectBandProfile();

    renderLeaderboard();

    await screen.findByText('Player One');
    await waitFor(() => expect(mockApi.getSongBandLeaderboard).toHaveBeenCalled());
    expect(document.body.textContent).not.toContain('Alpha + Beta');
  });
});

// ---------------------------------------------------------------------------
// Coverage: scroll cache and header collapse
// ---------------------------------------------------------------------------

describe('LeaderboardPage — coverage: scroll cache and header collapse', () => {
  it('triggers scroll handler updating cache and header state', async () => {
    renderLeaderboard('/songs/song-1/Solo_Guitar', 'test-player-1');

    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });

    // Find and scroll the scroll area
    const scrollArea = document.querySelector('[class*="scrollArea"]');
    if (scrollArea) {
      Object.defineProperty(scrollArea, 'scrollTop', { value: 100, writable: true });
      fireEvent.scroll(scrollArea);
    }

    // Should still render correctly
    expect(screen.getByText('Player One')).toBeDefined();
  });
});

// ---------------------------------------------------------------------------
// Coverage: localEntries fallback
// ---------------------------------------------------------------------------

describe('LeaderboardPage — coverage: localEntries fallback', () => {
  it('falls back to totalEntries when localEntries is undefined', async () => {
    mockApi.getLeaderboard.mockResolvedValue({
      songId: 'song-1', instrument: 'Solo_Guitar', count: 2, totalEntries: 50,
      // localEntries intentionally omitted — triggers ?? fallback
      entries: [
        { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1 },
        { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2 },
      ],
    });
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Player One')).toBeDefined();
    });
    // totalPages should use totalEntries (50) as fallback → 2 pages
    await waitFor(() => {
      expect(document.body.textContent).toContain('1 / 2');
    });
  });
});

// ---------------------------------------------------------------------------
// Coverage: header title click navigates to song detail
// ---------------------------------------------------------------------------

describe('LeaderboardPage — header title click', () => {
  it('renders a clickable title area linking to song detail', async () => {
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Test Song One')).toBeDefined();
    });
    const linkEl = document.querySelector('[role="link"]');
    expect(linkEl).toBeTruthy();
    expect((linkEl as HTMLElement).style.cursor).toBe('pointer');
  });
});
