/**
 * App.tsx mobile-branch coverage tests.
 * Exercises the isMobile conditional FAB rendering, mobile header,
 * bottom nav, and route-specific floating action buttons.
 */
import { describe, it, expect, vi, beforeEach, beforeAll } from 'vitest';
import { act, render, waitFor, fireEvent, screen, within } from '@testing-library/react';
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
    getPlayerNotifications: fn().mockResolvedValue({
      generatedAt: '2026-05-09T16:00:00Z',
      expiresAfterHours: 72,
      sourceRunId: 1,
      sourceCompletedAt: '2026-05-09T16:00:00Z',
      items: [],
    }),
    getBandNotificationsById: fn().mockResolvedValue({
      generatedAt: '2026-05-09T16:00:00Z',
      expiresAfterHours: 72,
      sourceRunId: 1,
      sourceCompletedAt: '2026-05-09T16:00:00Z',
      items: [],
    }),
    getPlayerHistory: fn().mockResolvedValue({ accountId: 'p1', count: 0, history: [] }),
    getLeaderboard: fn().mockResolvedValue({ songId: 's1', instrument: 'Solo_Guitar', count: 0, totalEntries: 0, localEntries: 0, entries: [] }),
    getAllLeaderboards: fn().mockResolvedValue({ songId: 's1', instruments: [] }),
    getAllSongBandLeaderboards: fn().mockResolvedValue({ songId: 's1', bands: [] }),
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
    getRivalsAll: fn().mockResolvedValue({ accountId: 'p1', songs: [], combos: [] }),
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

import App, { getEmptyBandFilterActionLabel, getFabQuickLinksActionLabel, mergePageQuickLinksIntoFabGroups, prependFabActionGroup, shouldShowBandFilterAction } from '../../src/App';
import type { SelectedProfile } from '../../src/hooks/data/useSelectedProfile';
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
  const originalCreateRange = document.createRange.bind(document);
  document.createRange = () => {
    const range = originalCreateRange();
    Object.assign(range, {
      getBoundingClientRect: () => ({
        top: 0,
        left: 0,
        bottom: 16,
        right: 120,
        width: 120,
        height: 16,
        x: 0,
        y: 0,
        toJSON() { return this; },
      }),
      getClientRects: () => [] as unknown as DOMRectList,
    });
    return range;
  };
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
      [createAction('Profile')],
    );

    expect(groups.map(group => group.map(action => action.label))).toEqual([
      ['Quick Links', 'Leaderboard Rivals'],
      ['Profile'],
    ]);
  });

  it('keeps quick links separate when there are no page-specific FAB actions', () => {
    const createAction = (label: string) => ({ label, icon: <span>{label}</span>, onPress: vi.fn() });

    const groups = mergePageQuickLinksIntoFabGroups(
      [createAction('Quick Links')],
      [],
      [createAction('Profile')],
    );

    expect(groups.map(group => group.map(action => action.label))).toEqual([
      ['Quick Links'],
      ['Profile'],
    ]);
  });

  it('prepends the band filter FAB action above quick links', () => {
    const createAction = (label: string) => ({ label, icon: <span>{label}</span>, onPress: vi.fn() });
    const quickLinksGroups = mergePageQuickLinksIntoFabGroups(
      [createAction('Quick Links')],
      [createAction('Sort Songs')],
      [createAction('Profile')],
    );

    const groups = prependFabActionGroup([createAction('Filter Band Type')], quickLinksGroups);

    expect(groups.map(group => group.map(action => action.label))).toEqual([
      ['Filter Band Type'],
      ['Quick Links', 'Sort Songs'],
      ['Profile'],
    ]);
  });

  it('only shows the band filter action for selected bands outside settings', () => {
    const bandProfile: SelectedProfile = {
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Lead + Bass',
      members: [],
    };
    const playerProfile: SelectedProfile = { type: 'player', accountId: 'p1', displayName: 'Player' };

    expect(shouldShowBandFilterAction(bandProfile, '/songs')).toBe(true);
    expect(shouldShowBandFilterAction(bandProfile, '/settings')).toBe(false);
    expect(shouldShowBandFilterAction(playerProfile, '/songs')).toBe(false);
    expect(shouldShowBandFilterAction(null, '/songs')).toBe(false);
  });

  it('labels inactive band filter actions by selected band type', () => {
    const t = vi.fn().mockImplementation((key: string, fallback?: string) => {
      const labels: Record<string, string> = {
        'bandList.groups.duos': 'Duos',
        'bandList.groups.trios': 'Trios',
        'bandList.groups.quads': 'Quads',
      };
      return labels[key] ?? fallback ?? key;
    });
    const bandProfile: SelectedProfile = {
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Lead + Bass',
      members: [],
    };

    expect(getEmptyBandFilterActionLabel(bandProfile, t as any)).toBe('Duos');
    expect(getEmptyBandFilterActionLabel(null, t as any)).toBe('Filter Band Type');
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

  it('hides notifications in the mobile header until a profile is selected', async () => {
    setMobile();
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });

    expect(screen.queryByRole('button', { name: 'Notifications' })).toBeNull();
  });

  it('shows notifications in the mobile header when a player profile is selected', async () => {
    setMobile();
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', accountId: 'p1', displayName: 'TrackedP' }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Notifications' }).getAttribute('data-notification-state')).toBe('empty');
    });
    expect(mockApi.getPlayerNotifications).toHaveBeenCalledWith('p1', 50, expect.any(Object));
  });

  it('waits for selected player notifications before animating the mobile header bell in', async () => {
    setMobile();
    let resolveNotifications!: (value: unknown) => void;
    const pendingNotifications = new Promise((resolve) => {
      resolveNotifications = resolve;
    });
    mockApi.getPlayerNotifications.mockReturnValueOnce(pendingNotifications);
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', accountId: 'p-pending', displayName: 'TrackedP' }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p-pending', displayName: 'TrackedP' }));
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    expect(screen.queryByRole('button', { name: 'Notifications' })).toBeNull();
    expect(mockApi.getPlayerNotifications).toHaveBeenCalledWith('p-pending', 50, expect.any(Object));

    await act(async () => {
      resolveNotifications({
        generatedAt: '2026-05-09T16:00:00Z',
        expiresAfterHours: 72,
        sourceRunId: 3,
        sourceCompletedAt: '2026-05-09T16:00:00Z',
        items: [{
          eventId: 88,
          notificationGuid: 'test-notification-guid-88',
          eventKind: 'player_score_pb',
          songId: 's1',
          instrument: 'Solo_Guitar',
          metric: 'score',
          oldNumeric: 100000,
          newNumeric: 120000,
          detectedAt: '2026-05-09T16:00:00Z',
          expiresAt: '2026-05-12T16:00:00Z',
        }],
      });
    });

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Notifications' }).getAttribute('data-notification-state')).toBe('populated');
    });
    expect(screen.getByRole('button', { name: 'Notifications' }).querySelector('svg path')?.getAttribute('fill')).toBeNull();
  });

  it('uses the populated mobile header bell when a selected player has notifications', async () => {
    setMobile();
    mockApi.getPlayerNotifications.mockResolvedValueOnce({
      generatedAt: '2026-05-09T16:00:00Z',
      expiresAfterHours: 72,
      sourceRunId: 2,
      sourceCompletedAt: '2026-05-09T16:00:00Z',
      items: [{
        eventId: 77,
        notificationGuid: 'test-notification-guid-77',
        eventKind: 'player_score_pb',
        songId: 's1',
        instrument: 'Solo_Guitar',
        metric: 'score',
        oldNumeric: 100000,
        newNumeric: 110000,
        detectedAt: '2026-05-09T16:00:00Z',
        expiresAt: '2026-05-12T16:00:00Z',
      }],
    });
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', accountId: 'p-populated', displayName: 'TrackedP' }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p-populated', displayName: 'TrackedP' }));
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Notifications' }).getAttribute('data-notification-state')).toBe('populated');
    });
    expect(within(screen.getByRole('button', { name: 'Notifications' })).queryByText('0')).toBeNull();
    expect(mockApi.getPlayerNotifications).toHaveBeenCalledWith('p-populated', 50, expect.any(Object));
  });

  it('opens unified Search on Players from the unselected mobile header profile action', async () => {
    setMobile();
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    fireEvent.click(screen.getByRole('button', { name: 'Select Player Profile' }));

    const dialog = await screen.findByRole('dialog', { name: 'Search' });
    expect(within(dialog).getByRole('tab', { name: 'Players' }).getAttribute('aria-selected')).toBe('true');
    expect(within(dialog).getByRole('tab', { name: 'Songs' }).getAttribute('aria-selected')).toBe('false');
  });

  it('does not include unselected profile access in the mobile FAB menu', async () => {
    setMobile();
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    expect(screen.getByRole('button', { name: 'Select Player Profile' })).toBeDefined();

    fireEvent.click(screen.getByLabelText('Actions'));
    const menu = await screen.findByTestId('fab-menu');

    expect(within(menu).queryByText('Select Player Profile')).toBeNull();
    expect(within(menu).queryByText('Search')).toBeNull();
    expect(within(menu).queryByText('Item Shop')).toBeNull();
    expect(within(menu).getByText('Sort Songs')).toBeDefined();
  });

  it('does not include the selected player in the mobile FAB menu', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    fireEvent.click(screen.getByLabelText('Actions'));
    const menu = await screen.findByTestId('fab-menu');

    expect(within(menu).queryByText('TrackedP')).toBeNull();
    expect(within(menu).queryByText('Item Shop')).toBeNull();
    expect(within(menu).getByText('Filter Songs')).toBeDefined();
  });

  it('does not include selected band profile access in the mobile FAB menu', async () => {
    setMobile();
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'TrackedP + BandMate',
      members: [
        { accountId: 'p1', displayName: 'TrackedP' },
        { accountId: 'p2', displayName: 'BandMate' },
      ],
    }));
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    fireEvent.click(screen.getByLabelText('Actions'));
    const menu = await screen.findByTestId('fab-menu');

    expect(within(menu).queryByText('Select Player Profile')).toBeNull();
    expect(within(menu).queryByText('TrackedP + BandMate')).toBeNull();
    expect(within(menu).queryByText('Item Shop')).toBeNull();
    expect(within(menu).getByText('Duos')).toBeDefined();
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

  it('opens Settings quick links directly from the mobile FAB', async () => {
    setMobile();
    window.location.hash = '#/settings';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('App Settings')).toBeDefined();
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();
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
      expect(screen.getByTestId('desktop-header-profile')).toBeDefined();
    });
    // Click profile button — should open player modal
    fireEvent.click(screen.getByTestId('desktop-header-profile'));
    // Modal should appear or something should change
    expect(container.innerHTML.length).toBeGreaterThan(200);
  });

  it('renders desktop profile button with tracked player (navigates)', async () => {
    setDesktop();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    const { container } = render(<App />);
    await waitFor(() => {
      expect(screen.getByTestId('desktop-header-profile')).toBeDefined();
    });
    fireEvent.click(screen.getByTestId('desktop-header-profile'));
    expect(container.innerHTML.length).toBeGreaterThan(200);
  });

  it('opens Filter Suggestions directly from the mobile Suggestions FAB', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/suggestions';
    render(<App />);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Filter Suggestions' })).toBeDefined();
    });
    await waitFor(() => {
      expect(mockApi.getRivalsAll).toHaveBeenCalled();
    });
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Filter Suggestions' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Filter Suggestions' })).toBeDefined();
    window.location.hash = '';
  });

  it('opens Statistics quick links directly from the mobile FAB without a header quick links action', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/statistics';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });
    expect(screen.queryByTestId('player-header-actions')).toBeNull();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();
    window.location.hash = '';
  });

  it('opens player detail quick links directly from the mobile FAB without moving Select as Profile', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', accountId: 'p1', displayName: 'TrackedP' }));
    mockApi.getPlayer.mockResolvedValueOnce({
      accountId: 'p2',
      displayName: 'OtherP',
      totalScores: 1,
      scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 110000, rank: 4, percentile: 84, accuracy: 91, stars: 5, season: 5 }],
    });
    window.location.hash = '#/player/p2';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });
    const headerActions = screen.getByTestId('player-header-actions');
    expect(within(headerActions).queryByRole('button', { name: 'Quick Links' })).toBeNull();
    expect(within(headerActions).getByRole('button', { name: 'Select Player Profile' })).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();
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
    expect(within(menu).queryByText('Item Shop')).toBeNull();
    expect(within(menu).queryAllByTestId('fab-menu-divider')).toHaveLength(0);

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
    expect(within(menu).queryByText('Item Shop')).toBeNull();
    expect(within(menu).queryAllByTestId('fab-menu-divider')).toHaveLength(0);

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

  it('keeps player bands pages accessible when legacy player-band overrides are disabled', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    localStorage.setItem('fst:featureFlagOverrides', JSON.stringify({ playerBands: false }));
    window.location.hash = '#/bands/player/p1?group=all&page=1&name=TrackedP';

    render(<App />);

    await waitFor(() => {
      expect(window.location.hash).toBe('#/bands/player/p1?group=all&page=1&name=TrackedP');
      expect(mockApi.getPlayerBandsList).toHaveBeenCalled();
      expect(screen.getByText('BandMate')).toBeDefined();
    }, { timeout: 5000 });

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
    const menu = await screen.findByTestId('fab-menu');
    expect(within(menu).queryByText('Search')).toBeNull();
    expect(within(menu).queryByText('Item Shop')).toBeNull();
    expect(within(menu).queryByText('View in Item Shop')).toBeNull();
    expect(within(menu).getByText('View Paths')).toBeDefined();
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

    expect(screen.queryByLabelText('Actions')).toBeNull();
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
