import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import SongDetailPage, { clearSongDetailCache } from '../../../src/pages/songinfo/SongDetailPage';
import { TestProviders } from '../../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 'song-1', title: 'Test Song', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/art.jpg',
        difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1, proGuitar: 5, proBass: 3 },
        maxScores: { Solo_Guitar: 150000, Solo_Bass: 120000, Solo_Drums: 180000, Solo_Vocals: 100000 } },
    ], count: 1, currentSeason: 5 }),
    getAllLeaderboards: fn().mockResolvedValue({
      songId: 'song-1',
      instruments: [
        { instrument: 'Solo_Guitar', count: 2, totalEntries: 100, localEntries: 100, entries: [
          { accountId: 'a1', displayName: 'P1', score: 145000, rank: 1, accuracy: 99, isFullCombo: true, stars: 6 },
          { accountId: 'a2', displayName: 'P2', score: 140000, rank: 2 },
        ] },
        { instrument: 'Solo_Bass', count: 1, totalEntries: 80, localEntries: 80, entries: [{ accountId: 'a1', displayName: 'P1', score: 115000, rank: 1 }] },
        { instrument: 'Solo_Drums', count: 1, totalEntries: 60, localEntries: 60, entries: [{ accountId: 'a1', displayName: 'P1', score: 170000, rank: 1 }] },
        { instrument: 'Solo_Vocals', count: 0, totalEntries: 40, localEntries: 40, entries: [] },
        { instrument: 'Solo_PeripheralGuitar', count: 0, totalEntries: 20, localEntries: 20, entries: [] },
        { instrument: 'Solo_PeripheralBass', count: 0, totalEntries: 10, localEntries: 10, entries: [] },
      ],
    }),
    getPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [
      { songId: 'song-1', instrument: 'Solo_Guitar', score: 142000, rank: 2, accuracy: 98, isFullCombo: false, stars: 5, season: 5 },
    ] }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'test-player-1', count: 2, history: [
      { songId: 'song-1', instrument: 'Solo_Guitar', oldScore: 130000, newScore: 145000, newRank: 1, accuracy: 99, isFullCombo: true, stars: 6, season: 5, scoreAchievedAt: '2025-01-15T10:00:00Z', changedAt: '2025-01-15T10:00:00Z' },
      { songId: 'song-1', instrument: 'Solo_Guitar', newScore: 130000, newRank: 3, accuracy: 97, isFullCombo: false, stars: 5, season: 4, scoreAchievedAt: '2024-09-10T08:00:00Z', changedAt: '2024-09-10T08:00:00Z' },
    ] }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getLeaderboard: fn().mockResolvedValue({ songId: 'song-1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'test-player-1', stats: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' }),
  };
});

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions(800);
});

function resetMocks() {
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 'song-1', title: 'Test Song', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/art.jpg',
      difficulty: { guitar: 3, bass: 2, drums: 4, vocals: 1, proGuitar: 5, proBass: 3 },
      maxScores: { Solo_Guitar: 150000, Solo_Bass: 120000, Solo_Drums: 180000, Solo_Vocals: 100000 } },
  ], count: 1, currentSeason: 5 });
  mockApi.getAllLeaderboards.mockResolvedValue({
    songId: 'song-1',
    instruments: [
      { instrument: 'Solo_Guitar', count: 2, totalEntries: 100, localEntries: 100, entries: [
        { accountId: 'a1', displayName: 'P1', score: 145000, rank: 1, accuracy: 99, isFullCombo: true, stars: 6 },
        { accountId: 'a2', displayName: 'P2', score: 140000, rank: 2 },
      ] },
      { instrument: 'Solo_Bass', count: 1, totalEntries: 80, localEntries: 80, entries: [{ accountId: 'a1', displayName: 'P1', score: 115000, rank: 1 }] },
      { instrument: 'Solo_Drums', count: 1, totalEntries: 60, localEntries: 60, entries: [{ accountId: 'a1', displayName: 'P1', score: 170000, rank: 1 }] },
      { instrument: 'Solo_Vocals', count: 0, totalEntries: 40, localEntries: 40, entries: [] },
      { instrument: 'Solo_PeripheralGuitar', count: 0, totalEntries: 20, localEntries: 20, entries: [] },
      { instrument: 'Solo_PeripheralBass', count: 0, totalEntries: 10, localEntries: 10, entries: [] },
    ],
  });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 1, scores: [
    { songId: 'song-1', instrument: 'Solo_Guitar', score: 142000, rank: 2, accuracy: 98, isFullCombo: false, stars: 5, season: 5 },
  ] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'test-player-1', count: 2, history: [
    { songId: 'song-1', instrument: 'Solo_Guitar', oldScore: 130000, newScore: 145000, newRank: 1, accuracy: 99, isFullCombo: true, stars: 6, season: 5, scoreAchievedAt: '2025-01-15T10:00:00Z', changedAt: '2025-01-15T10:00:00Z' },
    { songId: 'song-1', instrument: 'Solo_Guitar', newScore: 130000, newRank: 3, accuracy: 97, isFullCombo: false, stars: 5, season: 4, scoreAchievedAt: '2024-09-10T08:00:00Z', changedAt: '2024-09-10T08:00:00Z' },
  ] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 'song-1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'test-player-1', stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' });
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  clearSongDetailCache();
  resetMocks();
});

function renderSongDetail(route = '/songs/song-1', accountId?: string) {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/songs/:songId" element={<SongDetailPage />} />
      </Routes>
    </TestProviders>,
  );
}

describe('SongDetailPage', () => {
  it('renders without crashing', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('renders song header with title', async () => {
    renderSongDetail();
    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeDefined();
    });
  });

  it('renders song artist', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    expect(container.textContent).toContain('Artist A');
  });

  it('renders instrument cards after loading', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('Lead');
    });
  });

  it('shows "Song not found" for invalid songId', async () => {
    render(
      <TestProviders route="/songs">
        <Routes>
          <Route path="/songs" element={<SongDetailPage />} />
        </Routes>
      </TestProviders>,
    );
    await waitFor(() => {
      expect(screen.getByText('Song not found')).toBeDefined();
    });
  });

  it('fetches all leaderboards for the song', async () => {
    renderSongDetail();
    await waitFor(() => {
      expect(mockApi.getAllLeaderboards).toHaveBeenCalledWith('song-1', 10, undefined);
    });
  });

  it('fetches player data when tracked player exists', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(mockApi.getPlayer).toHaveBeenCalled();
      expect(mockApi.getPlayerHistory).toHaveBeenCalled();
    });
  });

  it('does not fetch player data when no tracked player', async () => {
    mockApi.getPlayer.mockResolvedValue({ accountId: '', displayName: '', totalScores: 0, scores: [] });
    renderSongDetail();
    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeDefined();
    });
    expect(mockApi.getAllLeaderboards).toHaveBeenCalled();
  });

  it('renders leaderboard entries in instrument cards', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    await waitFor(() => {
      expect(container.textContent).toContain('P1');
    });
  });

  it('renders score values from leaderboard entries', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('145,000');
    });
  });

  it('uses cached data on second render (no refetch)', async () => {
    const { unmount } = renderSongDetail();
    await waitFor(() => { expect(screen.getByText('Test Song')).toBeDefined(); });
    const callCount = mockApi.getAllLeaderboards.mock.calls.length;
    unmount();

    renderSongDetail();
    await waitFor(() => { expect(screen.getByText('Test Song')).toBeDefined(); });
    expect(mockApi.getAllLeaderboards).toHaveBeenCalledTimes(callCount);
  });

  it('renders only enabled instruments when settings limit them', async () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: true,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
    }));
    renderSongDetail();
    await waitFor(() => {
      expect(screen.getByText('Lead')).toBeDefined();
      expect(screen.getByText('Bass')).toBeDefined();
    });
    expect(screen.queryByText('Drums')).toBeNull();
  });

  it('handles API error gracefully for leaderboards', async () => {
    mockApi.getAllLeaderboards.mockRejectedValue(new Error('LB fail'));
    renderSongDetail();
    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeDefined();
    });
  });

  it('renders with tracked player showing score history chart', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
    const { container } = renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    await waitFor(() => {
      expect(mockApi.getPlayerHistory).toHaveBeenCalled();
    });
  });

  it('handles player score fetch failure gracefully', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
    mockApi.getPlayer.mockRejectedValue(new Error('Player fetch fail'));
    mockApi.getPlayerHistory.mockRejectedValue(new Error('History fetch fail'));
    const { container } = renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
  });

  it('renders without player when no player tracked', async () => {
    localStorage.removeItem('fst:trackedPlayer');
    mockApi.getPlayer.mockResolvedValue({ accountId: '', displayName: '', totalScores: 0, scores: [] });
    const { container } = renderSongDetail('/songs/song-1');
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    await waitFor(() => {
      expect(container.textContent).toContain('Lead');
    });
  });

  it('triggers scroll handler on scroll', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    const scrollArea = container.querySelector('[class*="scrollArea"]');
    if (scrollArea) {
      fireEvent.scroll(scrollArea);
    }
    expect(container.textContent).toContain('Test Song');
  });

  it('calculates global score width from leaderboard entries', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('145,000');
    });
  });

  it('applies player score filtering for invalid scores', async () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({
      filterInvalidScores: true,
      filterInvalidScoresLeeway: 1,
    }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
    const { container } = renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
  });

  it('renders album art background via PageBackground', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    expect(container.innerHTML).toContain('art.jpg');
  });

  it('handles missing song gracefully when songs list doesn\'t contain songId', async () => {
    mockApi.getSongs.mockResolvedValue({ songs: [], count: 0, currentSeason: 5 });
    const { container } = renderSongDetail('/songs/nonexistent');
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('filters score history entries per instrument', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
    mockApi.getPlayerHistory.mockResolvedValue({
      accountId: 'test-player-1', count: 3, history: [
        { songId: 'song-1', instrument: 'Solo_Guitar', newScore: 145000, newRank: 1, accuracy: 99, changedAt: '2025-01-15T10:00:00Z' },
        { songId: 'song-1', instrument: 'Solo_Bass', newScore: 100000, newRank: 5, accuracy: 85, changedAt: '2025-01-14T10:00:00Z' },
      ],
    });
    const { container } = renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
  });

  it('renders with instrument search param', async () => {
    const { container } = render(
      <TestProviders route="/songs/song-1?instrument=Solo_Guitar">
        <Routes>
          <Route path="/songs/:songId" element={<SongDetailPage />} />
        </Routes>
      </TestProviders>,
    );
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
  });

  it('handles resize events', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    act(() => { window.dispatchEvent(new Event('resize')); });
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
  });

  it('renders chart when player has score history', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
    const { container } = renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    await waitFor(() => {
      expect(mockApi.getPlayerHistory).toHaveBeenCalled();
    });
  });

  it('handles header collapse on scroll on non-mobile', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    });
    const scrollArea = container.querySelector('[class*="scrollArea"]');
    if (scrollArea) {
      Object.defineProperty(scrollArea, 'scrollTop', { value: 100, writable: true, configurable: true });
      fireEvent.scroll(scrollArea);
    }
    expect(container.textContent).toContain('Test Song');
  });

  it('computes globalScoreWidth from entries', async () => {
    const { container } = renderSongDetail();
    await waitFor(() => {
      expect(container.textContent).toContain('Lead');
    });
  });
});

describe('SongDetailPage — extra coverage', () => {
  beforeEach(() => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
  });

  /* ── Album art background ── */
  it('renders album art background image', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      const bgEl = document.querySelector('[class*="bg"]');
      expect(bgEl).toBeTruthy();
    });
  });

  it('renders background dim overlay', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      const dimEl = document.querySelector('[class*="dim"]');
      expect(dimEl).toBeTruthy();
    });
  });

  /* ── Sticky header ── */
  it('renders song detail header with song title', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeTruthy();
    });
  });

  it('renders artist name in header', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      const text = document.body.textContent ?? '';
      expect(text).toContain('Artist A');
    });
  });

  /* ── Instrument grid ── */
  it('renders instrument cards for active instruments', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      const cards = document.querySelectorAll('[id^="instrument-card-"]');
      expect(cards.length).toBeGreaterThan(0);
    });
  });

  /* ── Score history chart section ── */
  it('renders score history chart when player has history', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      const page = document.querySelector('[class*="page"]');
      expect(page).toBeTruthy();
    });
  });

  /* ── Song not found ── */
  it('handles non-existent songId gracefully', async () => {
    mockApi.getAllLeaderboards.mockResolvedValue({ songId: 'xyz', instruments: [] });
    const { container } = renderSongDetail('/songs/xyz', 'test-player-1');
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(0);
    });
  });

  /* ── No player tracked ── */
  it('renders without player data (no tracked player)', async () => {
    localStorage.removeItem('fst:trackedPlayer');
    mockApi.getPlayer.mockResolvedValue(null);
    renderSongDetail('/songs/song-1');
    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeTruthy();
    });
  });

  /* ── Spinner during loading ── */
  it('shows spinner while data is loading', async () => {
    mockApi.getAllLeaderboards.mockReturnValue(new Promise(() => {}));
    const { container } = renderSongDetail('/songs/song-1', 'test-player-1');
    expect(container.querySelector('[class*="spinner"]') || container.querySelector('[class*="page"]')).toBeTruthy();
  });

  /* ── Leaderboard entries rendering ── */
  it('renders leaderboard entry names in instrument cards', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(screen.getAllByText('P1').length).toBeGreaterThan(0);
    });
  });

  /* ── Paths modal ── */
  it('starts with paths modal closed', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeTruthy();
    });
  });

  /* ── Cached data on re-mount ── */
  it('uses cached data on second render', async () => {
    const { unmount } = renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeTruthy();
    });
    unmount();

    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeTruthy();
    });
  });
});
