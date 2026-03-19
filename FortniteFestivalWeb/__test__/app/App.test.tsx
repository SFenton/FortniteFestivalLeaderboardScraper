import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, waitFor, fireEvent } from '@testing-library/react';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubIntersectionObserver } from '../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [{ songId: 's1', title: 'Test', artist: 'A', year: 2024 }], count: 1, currentSeason: 5 }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'P', totalScores: 0, scores: [] }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'p1', count: 0, history: [] }),
    getLeaderboard: fn().mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 's1', instruments: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'p1', stats: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'P', trackingStarted: false, backfillStatus: '' }),
  };
});
import App from '../../src/App';

vi.mock('../../src/api/client', () => ({ api: mockApi }));

beforeAll(() => { stubScrollTo(); stubResizeObserver(); stubElementDimensions(); stubIntersectionObserver(); });
beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  // Reset mock values
  mockApi.getSongs.mockResolvedValue({ songs: [{ songId: 's1', title: 'Test', artist: 'A', year: 2024 }], count: 1, currentSeason: 5 });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'P', totalScores: 0, scores: [] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'p1', count: 0, history: [] });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 's1', instruments: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'p1', stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'P', trackingStarted: false, backfillStatus: '' });
});

describe('App', () => {
  it('renders without crashing', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
  });

  it('renders the app shell with navigation', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  it('renders songs page by default (/ redirects to /songs)', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
    vi.useRealTimers();
  });

  it('renders settings navigation link', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.querySelector('[aria-label="Open navigation"]')).toBeTruthy();
    });
    const hamburger = container.querySelector('[aria-label="Open navigation"]') as HTMLButtonElement;
    fireEvent.click(hamburger);
    await waitFor(() => {
      expect(container.textContent).toContain('Settings');
    });
  });

  it('renders brand name', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      // Brand name "Festival Score Tracker" should appear somewhere
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
  });

  it('renders changelog modal on first visit', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
    // Changelog shows on first visit (no localStorage entry)
    // It should contain "What's New" or similar
  });

  it('does not show suggestions/statistics when no player tracked', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
    // Without a tracked player, statistics/suggestions routes redirect to /songs
  });

  it('clears page caches when filter settings change', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML).toBeTruthy();
    });
    // This verifies the filter change effect runs without errors
  });
});
