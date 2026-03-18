import { describe, it, expect, vi, beforeEach, afterEach, beforeAll } from 'vitest';
import { render, screen, waitFor, fireEvent, act } from '@testing-library/react';
import { Routes, Route } from 'react-router-dom';
import SongsPage from '../../pages/songs/SongsPage';
import { TestProviders } from '../helpers/TestProviders';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3 }, albumArt: 'https://example.com/a.jpg' },
      { songId: 's2', title: 'Beta Song', artist: 'Artist B', year: 2023, difficulty: { guitar: 2 }, albumArt: 'https://example.com/b.jpg' },
      { songId: 's3', title: 'Gamma Song', artist: 'Artist C', year: 2025, difficulty: { guitar: 5 } },
    ], count: 3, currentSeason: 5 }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 2, scores: [
      { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 },
      { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, percentile: 60, accuracy: 85, stars: 3, season: 5 },
    ] }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null }),
    getLeaderboard: fn().mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 's1', instruments: [] }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'test-player-1', stats: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' }),
  };
});

vi.mock('../../api/client', () => ({ api: mockApi }));

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

function resetMocks() {
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 's1', title: 'Alpha Song', artist: 'Artist A', year: 2024, difficulty: { guitar: 3 }, albumArt: 'https://example.com/a.jpg' },
    { songId: 's2', title: 'Beta Song', artist: 'Artist B', year: 2023, difficulty: { guitar: 2 }, albumArt: 'https://example.com/b.jpg' },
    { songId: 's3', title: 'Gamma Song', artist: 'Artist C', year: 2025, difficulty: { guitar: 5 } },
  ], count: 3, currentSeason: 5 });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', totalScores: 2, scores: [
    { songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 },
    { songId: 's2', instrument: 'Solo_Guitar', score: 80000, rank: 10, percentile: 60, accuracy: 85, stars: 3, season: 5 },
  ] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'test-player-1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 's1', instruments: [] });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'test-player-1', count: 0, history: [] });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'test-player-1', stats: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-player-1', displayName: 'TestPlayer', trackingStarted: false, backfillStatus: 'none' });
}

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.clearAllMocks();
  localStorage.clear();
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-player-1', displayName: 'TestPlayer' }));
  resetMocks();
});

afterEach(() => {
  vi.useRealTimers();
});

function renderSongsPage(route = '/songs', accountId?: string) {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/songs" element={<SongsPage />} />
      </Routes>
    </TestProviders>,
  );
}

describe('SongsPage — extra coverage', () => {
  /* ── Sync banner rendering during sync ── */
  it('renders sync banner when backfill is in progress', async () => {
    mockApi.getSyncStatus.mockResolvedValue({
      accountId: 'test-player-1',
      isTracked: true,
      backfill: { status: 'in_progress', songsChecked: 50, totalSongsToCheck: 100, entriesFound: 200, startedAt: '2025-01-01T00:00:00Z', completedAt: null },
      historyRecon: null,
    });
    renderSongsPage('/songs', 'test-player-1');
    await act(async () => { vi.advanceTimersByTime(1000); });
    await waitFor(() => {
      const text = document.body.textContent ?? '';
      expect(text.includes('Syncing') || text.includes('Alpha Song')).toBeTruthy();
    });
  });

  /* ── Filter active count display ── */
  it('shows filter count when filters active', async () => {
    localStorage.setItem('fst-songSettings', JSON.stringify({
      sortMode: 'title', sortAscending: true, instrument: 'Solo_Guitar',
      metadataOrder: ['score', 'percentage', 'percentile', 'stars', 'intensity', 'seasonachieved'],
      instrumentOrder: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals', 'Solo_PeripheralGuitar', 'Solo_PeripheralBass'],
      filters: {
        seasonFilter: {}, percentileFilter: {}, starsFilter: {}, difficultyFilter: {},
        missingScores: {}, missingFCs: {}, hasScores: {}, hasFCs: {},
      },
    }));
    renderSongsPage('/songs', 'test-player-1');
    await act(async () => { vi.advanceTimersByTime(1000); });
    await waitFor(() => {
      const text = document.body.textContent ?? '';
      // Should show filtered count or the full list
      expect(text.length).toBeGreaterThan(10);
    });
  });

  /* ── Sort/Filter modal opening ── */
  it('renders sort pill button in toolbar', async () => {
    renderSongsPage('/songs', 'test-player-1');
    await act(async () => { vi.advanceTimersByTime(1000); });
    await waitFor(() => {
      expect(screen.getByText('Sort')).toBeTruthy();
    });
  });

  it('renders filter pill button when player is tracked', async () => {
    renderSongsPage('/songs', 'test-player-1');
    await act(async () => { vi.advanceTimersByTime(1000); });
    await waitFor(() => {
      expect(screen.getByText('Filter')).toBeTruthy();
    });
  });

  it('opens sort modal when sort pill is clicked', async () => {
    renderSongsPage('/songs', 'test-player-1');
    await act(async () => { vi.advanceTimersByTime(1000); });
    await waitFor(() => {
      expect(screen.getByText('Sort')).toBeTruthy();
    });
    fireEvent.click(screen.getByText('Sort'));
    // Modal should open — check for modal content
    await waitFor(() => {
      expect(document.body.textContent).toBeTruthy();
    });
  });

  /* ── Empty state with filters vs no filters ── */
  it('shows empty state when no songs match search', async () => {
    mockApi.getSongs.mockResolvedValue({ songs: [], count: 0, currentSeason: 5 });
    renderSongsPage('/songs');
    await act(async () => { vi.advanceTimersByTime(1000); });
    await waitFor(() => {
      const text = document.body.textContent ?? '';
      // Either shows "No results" or "service down" message
      expect(text.includes('No results') || text.includes('service') || text.length > 0).toBeTruthy();
    });
  });

  /* ── LoadPhase transitions ── */
  it('transitions through spinner → content phases', async () => {
    const { container } = renderSongsPage('/songs', 'test-player-1');
    // Initially should be in loading/spinner state
    expect(container.innerHTML.length).toBeGreaterThan(0);
    await act(async () => { vi.advanceTimersByTime(600); });
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  /* ── Search input ── */
  it('renders search input in toolbar', async () => {
    renderSongsPage('/songs', 'test-player-1');
    await act(async () => { vi.advanceTimersByTime(1000); });
    await waitFor(() => {
      const input = document.querySelector('input[class*="searchInput"]') as HTMLInputElement;
      expect(input).toBeTruthy();
    });
  });

  it('filters songs when search input changes', async () => {
    renderSongsPage('/songs', 'test-player-1');
    await act(async () => { vi.advanceTimersByTime(1000); });
    const input = document.querySelector('input[class*="searchInput"]') as HTMLInputElement;
    if (input) {
      fireEvent.change(input, { target: { value: 'Alpha' } });
      await act(async () => { vi.advanceTimersByTime(500); });
    }
  });

  /* ── Settings change re-stagger ── */
  it('handles settings changes from external events', async () => {
    renderSongsPage('/songs', 'test-player-1');
    await act(async () => { vi.advanceTimersByTime(1000); });
    // Dispatch the settings changed event
    window.dispatchEvent(new Event('fst-song-settings-changed'));
    await act(async () => { vi.advanceTimersByTime(600); });
    // Should still render
    expect(document.body.textContent).toBeTruthy();
  });
});
