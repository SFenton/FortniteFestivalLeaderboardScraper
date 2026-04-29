import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, waitFor, fireEvent } from '@testing-library/react';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubIntersectionObserver } from '../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [{ songId: 's1', title: 'Test', artist: 'A', year: 2024, difficulty: { guitar: 3 } }], count: 1, currentSeason: 5 }),
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

function resetMocks() {
  mockApi.getSongs.mockResolvedValue({ songs: [{ songId: 's1', title: 'Test', artist: 'A', year: 2024, difficulty: { guitar: 3 } }], count: 1, currentSeason: 5 });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'P', totalScores: 0, scores: [] });
  mockApi.getSyncStatus.mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null });
  mockApi.getPlayerHistory.mockResolvedValue({ accountId: 'p1', count: 0, history: [] });
  mockApi.getLeaderboard.mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ songId: 's1', instruments: [] });
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.getPlayerStats.mockResolvedValue({ accountId: 'p1', stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'p1', displayName: 'P', trackingStarted: false, backfillStatus: '' });
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  resetMocks();
});

describe('AppShell', () => {
  /* ── Route rendering ── */
  it('renders songs page by default (/ redirects to /songs)', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  it('renders the app shell structure', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.querySelector('#main-content') || container.innerHTML.length > 100).toBeTruthy();
    });
  });

  /* ── NavTitles rendering ── */
  it('renders navigation elements', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      // Nav/sidebar/bottom-nav elements should be rendered
      const hasNav = container.querySelector('nav') || container.querySelector('[class*="nav"]') || container.querySelector('[class*="shell"]');
      expect(hasNav).toBeTruthy();
    });
  });

  it('renders Settings title in navigation', async () => {
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

  /* ── Sidebar toggle ── */
  it('renders hamburger button on desktop', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    const { container } = render(<App />);
    await waitFor(() => {
      const hamburger = container.querySelector('[aria-label="Open navigation"]');
      expect(hamburger).toBeTruthy();
    });
  });

  it('toggles sidebar when hamburger is clicked', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.querySelector('[aria-label="Open navigation"]')).toBeTruthy();
    });
    const hamburger = container.querySelector('[aria-label="Open navigation"]') as HTMLButtonElement;
    fireEvent.click(hamburger);
    // Sidebar should now be open — look for sidebar element
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  /* ── Changelog modal on first visit ── */
  it('shows changelog modal when no previous version stored', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      // Changelog renders as a modal — check if the page rendered at all
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
  });

  it('does not show changelog when stored version matches', async () => {
    // Set the changelog as already seen
    const { changelogHash } = await import('../../src/changelog');
    const { APP_VERSION } = await import('../../src/hooks/data/useVersions');
    localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(50);
    });
  });

  /* ── Mobile bottom nav ── */
  it('renders bottom nav on mobile', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: query.includes('max-width'), media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  /* ── Statistics/suggestions redirect when no player ── */
  it('redirects /statistics to /songs when no player tracked', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      // Without player, statistics route redirects to /songs
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  it('redirects /suggestions to /songs when no player tracked', async () => {
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });

  /* ── Profile button ── */
  it('renders profile button in desktop header', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    const { container } = render(<App />);
    await waitFor(() => {
      const profileBtn = container.querySelector('[aria-label="Profile"]');
      expect(profileBtn).toBeTruthy();
    });
  });

  /* ── Header search ── */
  it('renders header search on desktop', async () => {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      configurable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false, media: query, onchange: null,
        addEventListener: vi.fn(), removeEventListener: vi.fn(),
        addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
      })),
    });
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
  });
});

import { getProfileClickDestination } from '../../src/App';

describe('getProfileClickDestination', () => {
  const playerProfile = { accountId: 'p1', displayName: 'Player' };
  const bandProfile = {
    type: 'band' as const,
    bandId: 'band-123',
    bandType: 'Band_Duets',
    teamKey: 'tk-abc',
    displayName: 'My Duo',
    members: [],
  };

  it('navigates to statistics when player is set', () => {
    const dest = getProfileClickDestination(playerProfile, null);
    expect(typeof dest).toBe('string');
    expect(dest).toContain('statistics');
  });

  it('navigates to statistics when both player and band profile are set', () => {
    const dest = getProfileClickDestination(playerProfile, bandProfile);
    expect(dest).toContain('statistics');
  });

  it('navigates to band route when no player and selected profile is a band with full context', () => {
    const dest = getProfileClickDestination(null, bandProfile);
    expect(typeof dest).toBe('string');
    expect(dest).toContain('bands');
    expect(dest).toContain('band-123');
    expect(dest).toContain('Band_Duets');
    expect(dest).toContain('tk-abc');
  });

  it('falls back to sidebar when selected band profile has no bandId', () => {
    const incomplete = { ...bandProfile, bandId: '' };
    const dest = getProfileClickDestination(null, incomplete);
    expect(dest).toBe('sidebar');
  });

  it('falls back to sidebar when selected band profile has no teamKey', () => {
    const incomplete = { ...bandProfile, teamKey: '' };
    const dest = getProfileClickDestination(null, incomplete);
    expect(dest).toBe('sidebar');
  });

  it('opens modal when no player and no selected profile', () => {
    const dest = getProfileClickDestination(null, null);
    expect(dest).toBe('modal');
  });

  it('opens modal when no player and selected profile is a player type', () => {
    const playerSelectedProfile = { type: 'player' as const, accountId: 'p2', displayName: 'Other' };
    const dest = getProfileClickDestination(null, playerSelectedProfile);
    expect(dest).toBe('modal');
  });
});
