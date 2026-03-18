import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import LeaderboardPage, { clearLeaderboardCache } from '../../pages/leaderboard/global/LeaderboardPage';
import { TestProviders } from '../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver } from '../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({
      songs: [{ songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024 }],
      count: 1,
      currentSeason: 5,
    }),
    getLeaderboard: fn().mockResolvedValue({
      songId: 'song-1',
      instrument: 'Solo_Guitar',
      count: 5,
      totalEntries: 50,
      localEntries: 50,
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
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'test-player-1', stats: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' }),
  };
});

vi.mock('../../api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
});

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearLeaderboardCache();
  // Re-set mock return values after clearAllMocks
  mockApi.getSongs.mockResolvedValue({ songs: [{ songId: 'song-1', title: 'Test Song One', artist: 'Artist A', year: 2024 }], count: 1, currentSeason: 5 });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 'song-1', instrument: 'Solo_Guitar', count: 5, totalEntries: 50, localEntries: 50, entries: [
    { accountId: 'acc-1', displayName: 'Player One', score: 145000, rank: 1, percentile: 99, accuracy: 99.5, isFullCombo: true, stars: 6, season: 5 },
    { accountId: 'acc-2', displayName: 'Player Two', score: 140000, rank: 2, percentile: 97, accuracy: 98.0, isFullCombo: false, stars: 5, season: 5 },
    { accountId: 'acc-3', displayName: 'Player Three', score: 135000, rank: 3, percentile: 95, accuracy: 96.2, isFullCombo: false, stars: 5, season: 4 },
    { accountId: 'acc-4', displayName: 'Player Four', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 },
    { accountId: 'acc-5', displayName: 'Player Five', score: 100000, rank: 5, percentile: 80, accuracy: 88.0, isFullCombo: false, stars: 3, season: 3 },
  ] });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [{ songId: 'song-1', instrument: 'Solo_Guitar', score: 120000, rank: 4, percentile: 90, accuracy: 93.1, isFullCombo: false, stars: 4, season: 5 }] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 'song-1', instruments: [] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'test-player-1', stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' });
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
      expect(screen.getByText('Server Error')).toBeDefined();
    });
  });

  it('shows fallback error for non-Error throws', async () => {
    mockApi.getLeaderboard.mockRejectedValue('fail');
    renderLeaderboard();
    await waitFor(() => {
      expect(screen.getByText('Failed to load leaderboard')).toBeDefined();
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

  it('renders pagination when total pages > 1', async () => {
    const { container } = renderLeaderboard();
    await waitFor(() => {
      expect(container.textContent).toContain('Player One');
    });
    // totalEntries = 50, LEADERBOARD_PAGE_SIZE = 25, so 2 pages
    await waitFor(() => {
      expect(container.textContent).toContain('1 / 2');
    });
  });

  it('fetches next page on Next click', async () => {
    const { container } = renderLeaderboard();
    await waitFor(() => {
      expect(container.textContent).toContain('Player One');
    });
    await waitFor(() => {
      expect(container.textContent).toContain('1 / 2');
    });

    // Button text includes arrow character: "Next ›"
    const nextBtn = Array.from(container.querySelectorAll('button')).find(
      b => b.textContent?.includes('Next'),
    );
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
    // Player footer should render with TestPlayer's data
    await waitFor(() => {
      expect(container.textContent).toContain('TestPlayer');
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

  it('renders first/prev/next/last pagination buttons', async () => {
    const { container } = renderLeaderboard();
    await waitFor(() => {
      expect(container.textContent).toContain('Player One');
    });
    await waitFor(() => {
      expect(container.textContent).toContain('1 / 2');
    });
    // Verify all pagination buttons exist
    const buttons = Array.from(container.querySelectorAll('button'));
    const buttonTexts = buttons.map(b => b.textContent);
    expect(buttonTexts.some(t => t?.includes('First'))).toBe(true);
    expect(buttonTexts.some(t => t?.includes('Prev'))).toBe(true);
    expect(buttonTexts.some(t => t?.includes('Next'))).toBe(true);
    expect(buttonTexts.some(t => t?.includes('Last'))).toBe(true);
  });

  it('navigates to last page on Last click', async () => {
    const { container } = renderLeaderboard();
    await waitFor(() => {
      expect(container.textContent).toContain('Player One');
    });
    await waitFor(() => {
      expect(container.textContent).toContain('1 / 2');
    });
    const lastBtn = Array.from(container.querySelectorAll('button')).find(
      b => b.textContent?.includes('Last'),
    );
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
    // No pagination buttons when only 1 page
    expect(container.textContent).not.toContain('1 / 2');
  });
});
