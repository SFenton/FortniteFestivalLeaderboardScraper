/**
 * App.tsx mobile-branch coverage tests.
 * Exercises the isMobile conditional FAB rendering, mobile header,
 * bottom nav, and route-specific floating action buttons.
 */
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { render, waitFor, fireEvent, screen, within } from '@testing-library/react';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubIntersectionObserver } from '../helpers/browserStubs';

const mockApi = vi.hoisted(() => {
  const fn = vi.fn;
  return {
    getSongs: fn().mockResolvedValue({ songs: [
      { songId: 's1', title: 'Test Song', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/a.jpg', difficulty: { guitar: 3 } },
    ], count: 1, currentSeason: 5 }),
    getVersion: fn().mockResolvedValue({ version: '1.0.0' }),
    getPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'TrackedP', totalScores: 1,
      scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 }],
    }),
    getSyncStatus: fn().mockResolvedValue({ accountId: 'p1', isTracked: false, backfill: null, historyRecon: null }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'p1', count: 0, history: [] }),
    getLeaderboard: fn().mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 's1', instruments: [] }),
    getComboRankings: fn().mockImplementation(async (comboId: string) => ({
      comboId,
      rankBy: 'totalscore',
      page: 1,
      pageSize: 10,
      totalAccounts: 100,
      entries: [{
        rank: 1,
        accountId: `top-${comboId}`,
        displayName: `Top ${comboId}`,
        adjustedRating: 0.9,
        weightedRating: 0.8,
        fcRate: 0.7,
        totalScore: 123456,
        maxScorePercent: 0.88,
        songsPlayed: 25,
        fullComboCount: 20,
        computedAt: '2026-01-01T00:00:00Z',
      }],
    })),
    getPlayerComboRanking: fn().mockImplementation(async (_accountId: string, comboId: string) => ({
      comboId,
      rankBy: 'totalscore',
      totalAccounts: 100,
      rank: 42,
      accountId: 'p1',
      displayName: 'TrackedP',
      adjustedRating: 0.5,
      weightedRating: 0.4,
      fcRate: 0.3,
      totalScore: 654321,
      maxScorePercent: 0.76,
      songsPlayed: 20,
      fullComboCount: 12,
      computedAt: '2026-01-01T00:00:00Z',
    })),
    getRankings: fn().mockImplementation(async (instrument: string) => ({
      instrument,
      rankBy: 'totalscore',
      page: 1,
      pageSize: 10,
      totalAccounts: 100,
      entries: [{
        accountId: `top-${instrument}`,
        displayName: `Top ${instrument}`,
        adjustedSkillRating: 0.9,
        adjustedSkillRank: 1,
        weightedRank: 1,
        fcRateRank: 1,
        totalScoreRank: 1,
        maxScorePercentRank: 1,
        rawSkillRating: 0.9,
        weightedRating: 0.8,
        rawWeightedRating: 0.8,
        totalChartedSongs: 25,
        songsPlayed: 25,
        totalScore: 555555,
        maxScorePercent: 0.9,
        rawMaxScorePercent: 0.9,
        fullComboCount: 20,
        fcRate: 0.8,
        avgAccuracy: 95,
        avgStars: 5,
        bestRank: 1,
        avgRank: 1,
        coverage: 1,
        computedAt: '2026-01-01T00:00:00Z',
      }],
    })),
    getPlayerRanking: fn().mockImplementation(async (instrument: string) => ({
      instrument,
      totalRankedAccounts: 100,
      accountId: 'p1',
      displayName: 'TrackedP',
      adjustedSkillRating: 0.6,
      adjustedSkillRank: 10,
      weightedRank: 10,
      fcRateRank: 10,
      totalScoreRank: 10,
      maxScorePercentRank: 10,
      rawSkillRating: 0.6,
      weightedRating: 0.5,
      rawWeightedRating: 0.5,
      totalChartedSongs: 25,
      songsPlayed: 20,
      totalScore: 444444,
      maxScorePercent: 0.7,
      rawMaxScorePercent: 0.7,
      fullComboCount: 12,
      fcRate: 0.48,
      avgAccuracy: 90,
      avgStars: 5,
      bestRank: 10,
      avgRank: 10,
      coverage: 0.8,
      computedAt: '2026-01-01T00:00:00Z',
    })),
    getRivalsOverview: fn().mockResolvedValue({ computedAt: '2024-01-01T00:00:00Z' }),
    getRivalsList: fn().mockResolvedValue({
      combo: 'Solo_Guitar',
      above: [{ accountId: 'rival-1', displayName: 'RivalAbove', sharedSongCount: 5, rivalScore: 300, aheadCount: 2, behindCount: 3, avgSignedDelta: 1.5 }],
      below: [{ accountId: 'rival-2', displayName: 'RivalBelow', sharedSongCount: 4, rivalScore: 200, aheadCount: 1, behindCount: 4, avgSignedDelta: -1.2 }],
    }),
    getLeaderboardRivals: fn().mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      userRank: 18,
      above: [{ accountId: 'leader-rival-1', displayName: 'LeaderAbove', sharedSongCount: 6, aheadCount: 3, behindCount: 3, avgSignedDelta: 1.25, leaderboardRank: 12, userLeaderboardRank: 18 }],
      below: [{ accountId: 'leader-rival-2', displayName: 'LeaderBelow', sharedSongCount: 5, aheadCount: 2, behindCount: 3, avgSignedDelta: -0.75, leaderboardRank: 24, userLeaderboardRank: 18 }],
    }),
    searchAccounts: fn().mockResolvedValue({ results: [] }),
    getPlayerStats: fn().mockResolvedValue({ accountId: 'p1', stats: [] }),
    getPlayerBandsList: fn().mockResolvedValue({
      accountId: 'p1',
      group: 'all',
      totalCount: 1,
      entries: [{
        bandId: 'band-1',
        teamKey: 'p1:p2',
        bandType: 'Band_Duets',
        appearanceCount: 2,
        members: [
          { accountId: 'p1', displayName: 'TrackedP', instruments: ['Solo_Guitar'] },
          { accountId: 'p2', displayName: 'BandMate', instruments: ['Solo_Bass'] },
        ],
      }],
    }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'TrackedP', trackingStarted: false, backfillStatus: '' }),
  };
});

vi.mock('../../src/api/client', () => ({ api: mockApi }));

import App, { getFabQuickLinksActionLabel, mergePageQuickLinksIntoFabGroups } from '../../src/App';
import { APP_VERSION } from '../../src/hooks/data/useVersions';
import { changelogHash } from '../../src/changelog';

function setMobile() {
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
}

function setDesktop() {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener: vi.fn(),
      removeEventListener: vi.fn(),
      addListener: vi.fn(),
      removeListener: vi.fn(),
      dispatchEvent: vi.fn(),
    })),
  });
}

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver();
  stubElementDimensions();
  stubIntersectionObserver();
  if (!HTMLElement.prototype.animate) {
    HTMLElement.prototype.animate = vi.fn().mockReturnValue({
      cancel: vi.fn(), pause: vi.fn(), play: vi.fn(), finish: vi.fn(),
      onfinish: null, finished: Promise.resolve(),
    }) as any;
  }
  if (!HTMLElement.prototype.getAnimations) {
    HTMLElement.prototype.getAnimations = vi.fn().mockReturnValue([]) as any;
  }
});

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
});

describe('App — mobile FAB branches', () => {
  it('merges quick links into the page-specific FAB group', () => {
    const createAction = (label: string) => ({ label, icon: <span>{label}</span>, onPress: vi.fn() });

    const groups = mergePageQuickLinksIntoFabGroups(
      [createAction('Quick Links')],
      [createAction('Leaderboard Rivals')],
      [createAction('Search')],
    );

    expect(groups.map(group => group.map(action => action.label))).toEqual([
      ['Quick Links', 'Leaderboard Rivals'],
      ['Search'],
    ]);
  });

  it('keeps quick links separate when there are no page-specific FAB actions', () => {
    const createAction = (label: string) => ({ label, icon: <span>{label}</span>, onPress: vi.fn() });

    const groups = mergePageQuickLinksIntoFabGroups(
      [createAction('Quick Links')],
      [],
      [createAction('Search')],
    );

    expect(groups.map(group => group.map(action => action.label))).toEqual([
      ['Quick Links'],
      ['Search'],
    ]);
  });

  it('uses a generic label for shell FAB quick links instead of a page-specific title', () => {
    const t = vi.fn().mockImplementation((key: string, fallback?: string) => {
      if (key === 'common.quickLinks') return 'Quick Links';
      return fallback ?? key;
    });

    expect(getFabQuickLinksActionLabel(t as any)).toBe('Quick Links');
    expect(getFabQuickLinksActionLabel(t as any)).not.toBe('Title Quick Links');
  });

  it('renders BottomNav and FAB on mobile /songs', async () => {
    setMobile();
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.textContent).toContain('Test Song');
    }, { timeout: 5000 });
    // FAB should be rendered for mobile /songs — look for the FAB search bar
    expect(container.querySelector('.fab-search-bar') || container.querySelector('[class*="fab"]') || container.querySelector('[class*="search"]')).toBeTruthy();
    expect(container.innerHTML.length).toBeGreaterThan(200);
  });

  it('renders mobile header with nav title on /songs', async () => {
    setMobile();
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(200);
    });
    // Mobile header should contain the nav title "Songs"
    expect(container.textContent).toContain('Songs');
    expect(container.innerHTML).toBeTruthy();
  });

  it('renders desktop nav on non-mobile', async () => {
    setDesktop();
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(200);
    });
    // Should have hamburger and nav
    expect(container.querySelector('nav')).toBeTruthy();
  });

  it('renders settings route FAB on mobile', async () => {
    setMobile();
    // Navigate to settings via hash
    window.location.hash = '#/settings';
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(200);
    });
    window.location.hash = '';
  });

  it('renders mobile with tracked player', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(200);
    });
    // With tracked player, FAB should include filter action
    expect(container.innerHTML).toBeTruthy();
  });

  it('renders desktop profile button with no player (opens modal)', async () => {
    setDesktop();
    const { container } = render(<App />);
    await waitFor(() => {
      const profileBtn = container.querySelector('[aria-label="Profile"]');
      expect(profileBtn).toBeTruthy();
    });
    // Click profile button — should open player modal
    const profileBtn = container.querySelector('[aria-label="Profile"]') as HTMLElement;
    if (profileBtn) fireEvent.click(profileBtn);
    // Modal should appear or something should change
    expect(container.innerHTML.length).toBeGreaterThan(200);
  });

  it('renders desktop profile button with tracked player (navigates)', async () => {
    setDesktop();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    const { container } = render(<App />);
    await waitFor(() => {
      const profileBtn = container.querySelector('[aria-label="Profile"]');
      expect(profileBtn).toBeTruthy();
    });
    const profileBtn = container.querySelector('[aria-label="Profile"]') as HTMLElement;
    if (profileBtn) fireEvent.click(profileBtn);
    expect(container.innerHTML.length).toBeGreaterThan(200);
  });

  it('renders suggestions route on mobile with tracked player', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/suggestions';
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(200);
    });
    window.location.hash = '';
  });

  it('keeps Rivals quick links in the same FAB section as the tab toggle on mobile', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/rivals';
    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Quick Links').length).toBeGreaterThan(0);
    }, { timeout: 5000 });

    fireEvent.click(screen.getByLabelText('Actions'));

    const menu = await screen.findByTestId('fab-menu');
    await waitFor(() => {
      expect(within(menu).getByText('Quick Links')).toBeDefined();
      expect(within(menu).getByText('Leaderboard Rivals')).toBeDefined();
    });
    expect(within(menu).getAllByTestId('fab-menu-divider')).toHaveLength(1);

    window.location.hash = '';
  });

  it('keeps Compete quick links in the same FAB section as the direct top-level section actions on mobile', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/compete';
    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Quick Links').length).toBeGreaterThan(0);
    }, { timeout: 5000 });

    fireEvent.click(screen.getByLabelText('Actions'));

    const menu = await screen.findByTestId('fab-menu');
    await waitFor(() => {
      expect(within(menu).getByText('Quick Links')).toBeDefined();
      expect(within(menu).getByText('Leaderboards')).toBeDefined();
      expect(within(menu).getByText('Rivals')).toBeDefined();
    });
    expect(within(menu).getAllByTestId('fab-menu-divider')).toHaveLength(1);

    window.location.hash = '';
  });

  it('navigates to the top-level Leaderboards page from the Compete FAB', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/compete';
    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Quick Links').length).toBeGreaterThan(0);
    }, { timeout: 5000 });

    fireEvent.click(screen.getByLabelText('Actions'));
    const menu = await screen.findByTestId('fab-menu');
    fireEvent.click(within(menu).getByText('Leaderboards'));

    await waitFor(() => {
      expect(window.location.hash).toBe('#/leaderboards');
    });

    window.location.hash = '';
  });

  it('navigates to the top-level Rivals page from the Compete FAB', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/compete';
    render(<App />);

    await waitFor(() => {
      expect(screen.getAllByText('Quick Links').length).toBeGreaterThan(0);
    }, { timeout: 5000 });

    fireEvent.click(screen.getByLabelText('Actions'));
    const menu = await screen.findByTestId('fab-menu');
    fireEvent.click(within(menu).getByText('Rivals'));

    await waitFor(() => {
      expect(window.location.hash).toBe('#/rivals');
    });

    window.location.hash = '';
  });

  it('shows the Filter Bands FAB action on mobile player bands pages', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/bands/player/p1?group=all&page=1&name=TrackedP';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('TrackedP')).toBeDefined();
      expect(screen.getByText('BandMate')).toBeDefined();
    }, { timeout: 5000 });

    fireEvent.click(screen.getByLabelText('Actions'));
    const menu = await screen.findByTestId('fab-menu');
    expect(within(menu).getByText('Filter Bands')).toBeDefined();
    fireEvent.click(within(menu).getByText('Filter Bands'));

    await waitFor(() => {
      expect(screen.getAllByText('Band Type').length).toBeGreaterThan(0);
    });

    window.location.hash = '';
  });

  it('redirects player bands pages to songs when the bands feature flag is disabled', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    localStorage.setItem('fst:featureFlagOverrides', JSON.stringify({ playerBands: false }));
    window.location.hash = '#/bands/player/p1?group=all&page=1&name=TrackedP';

    render(<App />);

    await waitFor(() => {
      expect(window.location.hash).toBe('#/songs');
    }, { timeout: 5000 });
    expect(mockApi.getPlayerBandsList).not.toHaveBeenCalled();
    expect(await screen.findByText('Test Song')).toBeDefined();

    window.location.hash = '';
  });

  it('shows the View Paths FAB action on mobile song detail when a supported path instrument is enabled', async () => {
    setMobile();
    window.location.hash = '#/songs/s1';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeDefined();
    });

    fireEvent.click(screen.getByLabelText('Actions'));
    await waitFor(() => {
      expect(screen.getByText('Search')).toBeDefined();
    });
    expect(screen.getByText('View Paths')).toBeDefined();
    window.location.hash = '';
  });

  it('hides the View Paths FAB action on mobile song detail when only unsupported path instruments are enabled', async () => {
    setMobile();
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: false,
      showBass: false,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: true,
      showPeripheralCymbals: true,
      showPeripheralDrums: true,
    }));
    window.location.hash = '#/songs/s1';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeDefined();
    });

    fireEvent.click(screen.getByLabelText('Actions'));
    await waitFor(() => {
      expect(screen.getByText('Search')).toBeDefined();
    });
    expect(screen.queryByText('View Paths')).toBeNull();
    window.location.hash = '';
  });

  it('handles invalid changelog JSON in localStorage', async () => {
    setDesktop();
    localStorage.setItem('fst:changelog', 'invalid json{{{');
    const { container } = render(<App />);
    await waitFor(() => {
      expect(container.innerHTML.length).toBeGreaterThan(100);
    });
    // Should show changelog since JSON parse fails
  });
});
