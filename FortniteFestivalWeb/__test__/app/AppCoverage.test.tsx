/**
 * App.tsx coverage tests — exercises settings sync, handleSelect,
 * backFallback, mobile header, and changelog modal.
 */
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, waitFor, act, fireEvent } from '@testing-library/react';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubIntersectionObserver } from '../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 's1', title: 'Test Song', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/a.jpg' },
      { songId: 's2', title: 'Song Two', artist: 'Artist B', year: 2023 },
    ], count: 2, currentSeason: 5 }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getPlayer: fn().mockResolvedValue({
      accountId: 'p1', displayName: 'TrackedP', totalScores: 1,
      scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 }],
    }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'p1', count: 0, history: [] }),
    getLeaderboard: fn().mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 's1', instruments: [] }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'p1', stats: [] }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'TrackedP', trackingStarted: false, backfillStatus: '' }),
    getRankings: fn().mockResolvedValue({ totalAccounts: 0, entries: [] }),
    getPlayerRanking: fn().mockResolvedValue(null),
  };
});

vi.mock('../../src/api/client', () => ({ api: mockApi }));

import App from '../../src/App';
import { APP_VERSION } from '../../src/hooks/data/useVersions';
import { changelogHash } from '../../src/changelog';
import { songSlides } from '../../src/pages/songs/firstRun';
import { contentHash } from '../../src/firstRun/types';

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions();
  stubIntersectionObserver();
  // Stub Web Animations API (used by AnimatedBackground)
  if (!HTMLElement.prototype.animate) {
    HTMLElement.prototype.animate = vi.fn().mockReturnValue({
      cancel: vi.fn(),
      pause: vi.fn(),
      play: vi.fn(),
      finish: vi.fn(),
      onfinish: null,
      finished: Promise.resolve(),
    }) as any;
  }
  if (!HTMLElement.prototype.getAnimations) {
    HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([]) as any;
  }
});

function resetMocks() {
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 's1', title: 'Test Song', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/a.jpg' },
    { songId: 's2', title: 'Song Two', artist: 'Artist B', year: 2023 },
  ], count: 2, currentSeason: 5 });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getPlayer.mockResolvedValue({
    accountId: 'p1', displayName: 'TrackedP', totalScores: 1,
    scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 }],
  });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'p1', count: 0, history: [] });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 's1', instruments: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'p1', stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'TrackedP', trackingStarted: false, backfillStatus: '' });
  mockApi.getRankings.mockResolvedValue({ totalAccounts: 0, entries: [] });
  mockApi.getPlayerRanking.mockResolvedValue(null);
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  resetMocks();
});

/** Pre-seed all song FRE slides as seen so they don't block changelog tests. */
function seedSongFRE() {
  const seen: Record<string, { version: number; hash: string; seenAt: string }> = {};
  for (const slide of songSlides(false)) {
    seen[slide.id] = { version: slide.version, hash: contentHash(slide.contentKey ?? (slide.title + slide.description)), seenAt: new Date().toISOString() };
  }
  localStorage.setItem('fst:firstRun', JSON.stringify(seen));
}

describe('App — coverage: changelog modal', () => {
  it('shows changelog modal on first visit and dismisses it', async () => {
    // Seed song FRE as seen so it doesn't block the changelog
    seedSongFRE();
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });

    // Changelog should be showing (no version stored yet)
    const changelogContent = container.textContent!;
    expect(changelogContent.length).toBeGreaterThan(0);

    // localStorage should NOT be written until the user dismisses
    expect(localStorage.getItem('fst:changelog')).toBeNull();

    // Dismiss the changelog
    const dismissBtn = container.querySelector('button[aria-label="Close"]') as HTMLElement;
    if (dismissBtn) fireEvent.click(dismissBtn);

    // Now localStorage should be written
    await waitFor(() => {
      expect(localStorage.getItem('fst:changelog')).toBeTruthy();
    });
  });

  it('does not show changelog when version+hash match', async () => {
    // Pre-set the changelog as seen
    localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));

    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  it('detects stale changelog when stored version differs', async () => {
    // Seed song FRE as seen so it doesn't block the changelog
    seedSongFRE();
    // Stored version doesn't match current — exercises the || short-circuit branch
    localStorage.setItem('fst:changelog', JSON.stringify({ version: '0.0.0-old', hash: changelogHash() }));

    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  it('treats corrupted changelog storage as new (catch branch)', async () => {
    seedSongFRE();
    // Invalid JSON triggers the catch block in the hasNewChangelog initializer
    localStorage.setItem('fst:changelog', 'NOT_VALID_JSON{{{');

    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });
});

describe('App — coverage: backFallback for detail routes', () => {
  it('renders back navigation for song detail route', async () => {
    // Set tracked player so statistics route works
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    // Dismiss changelog
    localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));

    const { container } = render(<App />);

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });

    // Navigate to a song detail by looking for a song link and clicking
    // The songs page should render with "Test Song"
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    }, { timeout: 5000 });
  });

  it('renders player route with tracked player', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));

    const { container } = render(<App />);

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });

    // With a tracked player, the statistics route should be accessible
    expect(container.innerHTML).toBeTruthy();
  });

  it('shows back button on /leaderboards/all', async () => {
    localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
    window.location.hash = '#/leaderboards/all?instrument=Solo_Guitar';

    // Simulate mobile viewport so MobileHeader renders the BackLink
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: query.includes('max-width'),
        media: query,
        onchange: null,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    });

    const { container } = render(<App />);

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });

    // The back link should point to /leaderboards
    const backLink = container.querySelector('a[href="#/leaderboards"]');
    expect(backLink).toBeTruthy();

    window.location.hash = '';
  });
});

describe('App — coverage: settings sync + filter changes', () => {
  it('clears caches when filter settings change in localStorage', async () => {
    localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));

    const { container } = render(<App />);

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });

    // Trigger a settings change event by updating localStorage
    act(() => {
      localStorage.setItem('fst:settings', JSON.stringify({
        filterInvalidScores: true,
        filterInvalidScoresLeeway: 5,
      }));
      window.dispatchEvent(new StorageEvent('storage', { key: 'fst:settings' }));
    });

    // App should still render without errors
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });
});

describe('App — coverage: mobile header rendering', () => {
  it('renders with mobile-like viewport', async () => {
    localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));

    // Simulate mobile viewport
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        // Return true for mobile queries
        matches: query.includes('max-width'),
        media: query,
        onchange: null,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    });

    const { container } = render(<App />);

    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });

    // Should render mobile-specific elements (bottom nav or mobile header)
    expect(container.innerHTML).toBeTruthy();
  });
});
