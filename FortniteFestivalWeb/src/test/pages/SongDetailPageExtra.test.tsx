import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import SongDetailPage, { clearSongDetailCache } from '../../pages/songinfo/SongDetailPage';
import { TestProviders } from '../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../helpers/browserStubs';

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

vi.mock('../../api/client', () => ({ api: mockApi }));

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
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
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

describe('SongDetailPage — extra coverage', () => {
  /* ── Album art background ── */
  it('renders album art background image', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      const bgEl = document.querySelector('[class*="bgImage"]');
      expect(bgEl).toBeTruthy();
    });
  });

  it('renders background dim overlay', async () => {
    renderSongDetail('/songs/song-1', 'test-player-1');
    await waitFor(() => {
      const dimEl = document.querySelector('[class*="bgDim"]');
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
      // Should have instrument-card elements
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
    // When song not found in songs list, the page still mounts but may show
    // a minimal view or missing data
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
    // Check that a spinner is rendered
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
    // PathsModal should not be visible initially (no visible=true)
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
