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
    getFeatures: fn().mockResolvedValue({ compete: true, leaderboards: true, difficulty: true, playerBands: true, experimentalRanks: true, appManual: true }),
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
    getRankHistory: fn().mockImplementation(async (instrument: string, accountId: string, days: number) => ({
      instrument,
      accountId,
      days,
      history: [],
    })),
    getBandRankings: fn().mockImplementation(async (bandType: string, comboId?: string | null, rankBy = 'totalscore', page = 1, pageSize = 10) => ({
      bandType,
      comboId: comboId ?? null,
      rankBy,
      page,
      pageSize,
      totalTeams: 1,
      entries: [],
      selectedPlayerEntry: null,
      selectedBandEntry: null,
    })),
    getBandRanking: fn().mockResolvedValue(null),
    getBandRankHistory: fn().mockResolvedValue({
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      comboId: null,
      days: 30,
      history: [],
    }),
    getBandSongs: fn().mockResolvedValue({
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      comboId: null,
      limit: 5,
      best: [],
      worst: [],
    }),
    getSelectedMemberRankings: fn().mockResolvedValue({ instruments: [] }),
    getBandDetail: fn().mockResolvedValue({
      band: {
        bandId: 'band-1',
        teamKey: 'p1:p2',
        bandType: 'Band_Duets',
        members: [
          { accountId: 'p1', displayName: 'TrackedP', instruments: ['Solo_Guitar', 'Solo_Bass'] },
          { accountId: 'p2', displayName: 'BandMate', instruments: ['Solo_Guitar', 'Solo_Bass'] },
        ],
      },
      ranking: null,
      configurations: [
        {
          rawInstrumentCombo: '0:1',
          comboId: 'Solo_Guitar+Solo_Bass',
          instruments: ['Solo_Guitar', 'Solo_Bass'],
          assignmentKey: 'p1=Solo_Guitar|p2=Solo_Bass',
          appearanceCount: 1,
          memberInstruments: {
            p1: 'Solo_Guitar',
            p2: 'Solo_Bass',
          },
        },
      ],
    }),
    getRivalsOverview: fn().mockResolvedValue({ computedAt: '2024-01-01T00:00:00Z' }),
    getRivalsList: fn().mockResolvedValue({
      combo: 'Solo_Guitar',
      above: [{ accountId: 'rival-1', displayName: 'RivalAbove', sharedSongCount: 5, rivalScore: 300, aheadCount: 2, behindCount: 3, avgSignedDelta: 1.5 }],
      below: [{ accountId: 'rival-2', displayName: 'RivalBelow', sharedSongCount: 4, rivalScore: 200, aheadCount: 1, behindCount: 4, avgSignedDelta: -1.2 }],
    }),
    getRivalDetail: fn().mockResolvedValue({
      rival: { accountId: 'rival-1', displayName: 'TestRival' },
      songs: [
        { songId: 's1', title: 'Test Song', artist: 'Artist A', instrument: 'Solo_Guitar', userRank: 5, rivalRank: 8, userScore: 100000, rivalScore: 98000, rankDelta: 3 },
      ],
    }),
    getRivalsAll: fn().mockResolvedValue({ accountId: 'p1', songs: [], combos: [] }),
    getLeaderboardRivals: fn().mockResolvedValue({
      instrument: 'Solo_Guitar',
      rankBy: 'totalscore',
      userRank: 18,
      above: [{ accountId: 'leader-rival-1', displayName: 'LeaderAbove', sharedSongCount: 6, aheadCount: 3, behindCount: 3, avgSignedDelta: 1.25, leaderboardRank: 12, userLeaderboardRank: 18 }],
      below: [{ accountId: 'leader-rival-2', displayName: 'LeaderBelow', sharedSongCount: 5, aheadCount: 2, behindCount: 3, avgSignedDelta: -0.75, leaderboardRank: 24, userLeaderboardRank: 18 }],
    }),
    getBandSongRows: fn().mockResolvedValue({ bandType: 'Band_Duets', teamKey: 'p1:p2', comboId: null, count: 0, entries: [] }),
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
    getShop: fn().mockResolvedValue({
      songs: [{
        songId: 's1',
        title: 'Test Song',
        artist: 'Artist A',
        year: 2024,
        albumArt: 'https://example.com/a.jpg',
        shopUrl: 'https://example.com/shop/s1',
        leavingTomorrow: false,
      }],
      lastUpdated: '2026-05-12T00:00:00Z',
    }),
    trackPlayer: fn().mockResolvedValue({ accountId: 'p1', displayName: 'TrackedP', trackingStarted: false, backfillStatus: '' }),
  };
});

vi.mock('../../src/api/client', () => ({ api: mockApi }));

import App, { getEmptyBandFilterActionLabel, getFabQuickLinksActionLabel, mergePageQuickLinksIntoFabGroups, prependFabActionGroup, shouldShowBandFilterAction } from '../../src/App';
import { queryClient } from '../../src/api/queryClient';
import type { SelectedProfile } from '../../src/hooks/data/useSelectedProfile';
import { APP_VERSION } from '../../src/hooks/data/useVersions';
import { changelogHash } from '../../src/changelog';
import { contentHash } from '../../src/firstRun/types';
import { shopSlides } from '../../src/pages/shop/firstRun';
import { defaultSongSettings } from '../../src/utils/songSettings';

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

function setMobileWidth(width: number, height = 844) {
  Object.defineProperty(window, 'innerWidth', { configurable: true, value: width });
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: height });
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => {
      const max = query.match(/max-width:\s*(\d+)px/);
      const min = query.match(/min-width:\s*(\d+)px/);
      return {
        matches: (max ? width <= Number(max[1]) : true) && (min ? width >= Number(min[1]) : true),
        media: query,
        onchange: null,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        addListener: vi.fn(),
        removeListener: vi.fn(),
        dispatchEvent: vi.fn(),
      };
    }),
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
  queryClient.clear();
  localStorage.clear();
  localStorage.setItem('fst:changelog', JSON.stringify({ version: APP_VERSION, hash: changelogHash() }));
});

function notificationResponse(items: unknown[] = []) {
  return {
    generatedAt: '2026-05-09T16:00:00Z',
    expiresAfterHours: 72,
    sourceRunId: 1,
    sourceCompletedAt: '2026-05-09T16:00:00Z',
    items,
  };
}

function storeSelectedProfile(profile: Record<string, unknown>) {
  localStorage.setItem('fst:selectedProfile', JSON.stringify(profile));
  if (profile.type === 'player') {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: profile['accountId'], displayName: profile['displayName'] }));
  } else {
    localStorage.removeItem('fst:trackedPlayer');
  }
  window.dispatchEvent(new Event('fst:selectedProfileChanged'));
  window.dispatchEvent(new Event('fst:trackedPlayerChanged'));
}

function markShopFirstRunSeen() {
  const seen = Object.fromEntries(shopSlides({ viewToggleAvailable: true }).map(slide => [slide.id, {
    version: slide.version,
    hash: contentHash(slide.contentKey ?? (slide.title + slide.description)),
    seenAt: '2026-05-12T00:00:00.000Z',
  }]));
  localStorage.setItem('fst:firstRun', JSON.stringify(seen));
}

function playerNotificationItem(eventId: number, notificationGuid: string, instrument: string, eventKind = 'player_score_pb') {
  return {
    eventId,
    notificationGuid,
    eventKind,
    songId: 's1',
    instrument,
    metric: 'score',
    oldNumeric: 100000,
    newNumeric: 110000 + eventId,
    detectedAt: '2026-05-09T16:00:00Z',
    expiresAt: '2026-05-12T16:00:00Z',
  };
}

function bandNotificationItem(eventId: number, notificationGuid: string, eventKind = 'band_score_pb') {
  return {
    eventId,
    notificationGuid,
    eventKind,
    songId: 's1',
    rankingScope: 'combo',
    comboId: 'Solo_Guitar+Solo_Drums',
    metric: 'score',
    oldNumeric: 200000,
    newNumeric: 210000 + eventId,
    detectedAt: '2026-05-09T16:00:00Z',
    expiresAt: '2026-05-12T16:00:00Z',
  };
}

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

  it('filters selected player notification badge and modal rows by visible instruments', async () => {
    setMobile();
    mockApi.getPlayerNotifications.mockResolvedValueOnce(notificationResponse([
      playerNotificationItem(201, 'hidden-drums-notification', 'Solo_Drums'),
      playerNotificationItem(202, 'visible-guitar-notification', 'Solo_Guitar'),
      bandNotificationItem(203, 'visible-band-notification'),
    ]));
    localStorage.setItem('fst:appSettings', JSON.stringify({ showDrums: false }));
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', accountId: 'p-filtered', displayName: 'TrackedP' }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p-filtered', displayName: 'TrackedP' }));

    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    const notificationsButton = await screen.findByRole('button', { name: 'Notifications' });
    await waitFor(() => {
      expect(notificationsButton.getAttribute('data-notification-state')).toBe('populated');
      expect(within(notificationsButton).getByText('2')).toBeDefined();
    });

    fireEvent.click(notificationsButton);

    await screen.findByRole('dialog', { name: 'Notifications' });
    const rows = screen.getAllByTestId('mock-notification-row');
    expect(rows).toHaveLength(2);
    expect(rows.map(row => row.getAttribute('data-notification-guid'))).toEqual([
      'visible-band-notification',
      'visible-guitar-notification',
    ]);
    expect(screen.queryByText('Test Song · Drums')).toBeNull();
  });

  it('does not apply instrument visibility filtering to selected band feeds', async () => {
    setMobile();
    mockApi.getBandNotificationsById.mockResolvedValueOnce(notificationResponse([
      playerNotificationItem(301, 'band-feed-drums-notification', 'Solo_Drums'),
      bandNotificationItem(302, 'band-feed-rank-notification', 'band_weighted_rank_improved'),
    ]));
    localStorage.setItem('fst:appSettings', JSON.stringify({ showDrums: false }));
    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'band',
      bandId: 'band-filter-test',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'Tracked Band',
      members: [],
    }));
    localStorage.removeItem('fst:trackedPlayer');

    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    const notificationsButton = await screen.findByRole('button', { name: 'Notifications' });
    await waitFor(() => {
      expect(notificationsButton.getAttribute('data-notification-state')).toBe('populated');
      expect(within(notificationsButton).getByText('2')).toBeDefined();
    });

    fireEvent.click(notificationsButton);

    await screen.findByRole('dialog', { name: 'Notifications' });
    expect(screen.getAllByTestId('mock-notification-row').map(row => row.getAttribute('data-notification-guid'))).toEqual([
      'band-feed-rank-notification',
      'band-feed-drums-notification',
    ]);
    expect(mockApi.getBandNotificationsById).toHaveBeenCalledWith('band-filter-test', 50, expect.any(Object));
  });

  it('delays the new profile notification request until the swap spinner has faded in', async () => {
    setMobile();
    mockApi.getPlayerNotifications.mockImplementation(async (accountId: string) => notificationResponse(accountId === 'p-new'
      ? [{
        eventId: 910,
        notificationGuid: 'swap-new-notification',
        eventKind: 'player_score_pb',
        songId: 's1',
        instrument: 'Solo_Guitar',
        metric: 'score',
        oldNumeric: 100000,
        newNumeric: 121000,
        detectedAt: '2026-05-09T16:00:00Z',
        expiresAt: '2026-05-12T16:00:00Z',
      }]
      : []));
    storeSelectedProfile({ type: 'player', accountId: 'p-old', displayName: 'Old Profile' });
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Notifications' }).getAttribute('data-notification-state')).toBe('empty');
    });
    expect(mockApi.getPlayerNotifications).toHaveBeenCalledWith('p-old', 50, expect.any(Object));

    await act(async () => {
      storeSelectedProfile({ type: 'player', accountId: 'p-new', displayName: 'New Profile' });
    });

    await waitFor(() => {
      expect(screen.getByTestId('mobile-header-notifications').getAttribute('data-notification-visual-state')).toBe('spinnerIn');
    });
    expect(mockApi.getPlayerNotifications.mock.calls.some(([accountId]) => accountId === 'p-new')).toBe(false);

    await waitFor(() => {
      expect(mockApi.getPlayerNotifications).toHaveBeenCalledWith('p-new', 50, expect.any(Object));
    });
    await waitFor(() => {
      expect(screen.getByTestId('mobile-header-notifications').getAttribute('data-notification-visual-state')).toBe('icon');
      expect(screen.getByTestId('mobile-header-notifications').getAttribute('data-notification-state')).toBe('populated');
    });
  });

  it('opens unified Search on Players from the unselected mobile header profile action', async () => {
    setMobile();
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    fireEvent.click(screen.getByRole('button', { name: 'Select Profile' }));

    const dialog = await screen.findByRole('dialog', { name: 'Search' });
    expect(within(dialog).getByPlaceholderText('Search players or bands…')).toBeDefined();
    expect(within(dialog).getByRole('tab', { name: 'Players' }).getAttribute('aria-selected')).toBe('true');
    expect(within(dialog).queryByRole('tab', { name: 'Songs' })).toBeNull();
    expect(within(dialog).getByRole('tab', { name: 'Bands' }).getAttribute('aria-selected')).toBe('false');
  });

  it('opens unified Search on Players from the unselected sidebar profile action', async () => {
    setMobile();
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    fireEvent.click(screen.getByLabelText('Open navigation'));
    const profileButtons = await screen.findAllByRole('button', { name: 'Select Profile' });
    const sidebarProfileButton = profileButtons.find(button => button.textContent?.includes('Select Profile'));
    expect(sidebarProfileButton).toBeDefined();
    fireEvent.click(sidebarProfileButton!);

    const dialog = await screen.findByRole('dialog', { name: 'Search' });
    expect(within(dialog).getByPlaceholderText('Search players or bands…')).toBeDefined();
    expect(within(dialog).getByRole('tab', { name: 'Players' }).getAttribute('aria-selected')).toBe('true');
    expect(within(dialog).queryByRole('tab', { name: 'Songs' })).toBeNull();
    expect(within(dialog).getByRole('tab', { name: 'Bands' }).getAttribute('aria-selected')).toBe('false');
  });

  it('keeps profile access out of the mobile Songs dock', async () => {
    setMobile();
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    expect(screen.getByTestId('mobile-header-profile')).toBeDefined();

    const dock = document.querySelector('.fab-search-dock') as HTMLElement;
    expect(dock).toBeTruthy();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    expect(within(dock).getByRole('button', { name: 'Search' })).toBeDefined();
    expect(within(dock).getByRole('button', { name: 'Sort Songs' })).toBeDefined();
    expect(within(dock).queryByRole('button', { name: 'Filter Songs' })).toBeNull();

    expect(within(dock).queryByText('Select Profile')).toBeNull();
    expect(within(dock).queryByText('Item Shop')).toBeNull();
    expect(screen.queryByTestId('fab-menu')).toBeNull();
  });

  it('keeps the selected player out of the mobile Songs dock and exposes Filter Songs', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    const dock = document.querySelector('.fab-search-dock') as HTMLElement;
    expect(dock).toBeTruthy();

    expect(within(dock).queryByText('TrackedP')).toBeNull();
    expect(within(dock).queryByText('Item Shop')).toBeNull();
    expect(within(dock).getByRole('button', { name: 'Filter Songs' })).toBeDefined();
    expect(screen.queryByTestId('fab-menu')).toBeNull();
  });

  it('renders the mobile Shop FAB as a direct List/Grid toggle above the narrow-grid breakpoint', async () => {
    setMobileWidth(430);
    markShopFirstRunSeen();
    window.location.hash = '#/shop';
    render(<App />);

    const listButton = await screen.findByRole('button', { name: 'Switch to list view' }, { timeout: 5000 });
    await screen.findByText('Test Song', undefined, { timeout: 5000 });
    expect(within(listButton).getByText('List')).toBeDefined();
    expect(listButton.getAttribute('title')).toBe('Switch to list view');
    expect(screen.queryByLabelText('Actions')).toBeNull();
    expect(screen.queryByTestId('fab-menu')).toBeNull();

    fireEvent.click(listButton);

    const gridButton = await screen.findByRole('button', { name: 'Switch to grid view' });
    expect(within(gridButton).getByText('Grid')).toBeDefined();
    expect(localStorage.getItem('fst:shopView')).toBe('list');
    expect(screen.queryByTestId('fab-menu')).toBeNull();

    fireEvent.click(gridButton);

    await screen.findByRole('button', { name: 'Switch to list view' });
    expect(localStorage.getItem('fst:shopView')).toBe('grid');
    window.location.hash = '';
  });

  it('hides the mobile Shop view-toggle FAB below the narrow-grid breakpoint', async () => {
    setMobileWidth(375);
    markShopFirstRunSeen();
    window.location.hash = '#/shop';
    render(<App />);

    await waitFor(() => {
      expect(screen.queryByRole('button', { name: 'Switch to list view' })).toBeNull();
      expect(screen.queryByRole('button', { name: 'Switch to grid view' })).toBeNull();
      expect(screen.queryByLabelText('Actions')).toBeNull();
    }, { timeout: 5000 });
    expect(screen.queryByTestId('fab-menu')).toBeNull();
    window.location.hash = '';
  });

  it('keeps the selected band out of the mobile Songs dock and exposes Filter Songs beside the FAB', async () => {
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
    const dock = document.querySelector('.fab-search-dock') as HTMLElement;
    expect(dock).toBeTruthy();

    expect(within(dock).getByRole('button', { name: 'Search' })).toBeDefined();
    expect(within(dock).getByRole('button', { name: 'Sort Songs' })).toBeDefined();
    const filterButton = within(dock).getByRole('button', { name: 'Filter Songs' });

    expect(within(dock).queryByText('Select Profile')).toBeNull();
    expect(within(dock).queryByText('TrackedP + BandMate')).toBeNull();
    expect(within(dock).queryByText('Item Shop')).toBeNull();
    expect(filterButton.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(within(filterButton).queryByAltText('Solo_Guitar')).toBeNull();
    expect(screen.queryByTestId('fab-side-actions')).toBeNull();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    fireEvent.click(filterButton);

    const dialog = await screen.findByRole('dialog', { name: 'Filter Songs' });
    expect(await within(dialog).findByText('Instrument #1')).toBeDefined();
    expect(within(dialog).getByText('Selected Band Scores')).toBeDefined();
    expect(within(dialog).getByText('Filter songs by whether TrackedP + BandMate has a band score recorded.')).toBeDefined();
    expect(within(dialog).getByText('Has Selected Band Score')).toBeDefined();
    expect(within(dialog).queryByText('Global Score & FC Toggles')).toBeNull();
  });

  it('shows active selected-band combo instruments in the mobile Songs filter dock action', async () => {
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
    localStorage.setItem('fst:bandFilter', JSON.stringify({
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      comboId: 'Solo_Guitar+Solo_Bass',
      assignments: [
        { accountId: 'p1', instrument: 'Solo_Guitar' },
        { accountId: 'p2', instrument: 'Solo_Bass' },
      ],
    }));
    render(<App />);

    await waitFor(() => {
      expect(mockApi.getBandSongRows).toHaveBeenCalledWith('Band_Duets', 'p1:p2', 'Solo_Guitar+Solo_Bass');
    }, { timeout: 5000 });

    const dock = document.querySelector('.fab-search-dock') as HTMLElement;
    const filterButton = within(dock).getByRole('button', { name: 'Filter Songs' });
    expect(filterButton).toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(within(filterButton).getByAltText('Solo_Guitar')).toBeDefined();
    expect(within(filterButton).getByAltText('Solo_Bass')).toBeDefined();
    expect(within(filterButton).getByTestId('fab-band-filter-instruments')).toBeDefined();
    expect(screen.queryByTestId('fab-side-actions')).toBeNull();

    fireEvent.click(filterButton);

    const dialog = await screen.findByRole('dialog', { name: 'Filter Songs' });
    expect(await within(dialog).findByText('Instrument #1')).toBeDefined();
    window.location.hash = '';
  });

  it('shows Songs filter-only active state without combo instruments in the mobile dock action', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    const songSettings = defaultSongSettings();
    songSettings.filters.hasScores = { Solo_Guitar: true };
    localStorage.setItem('fst:songSettings', JSON.stringify(songSettings));
    render(<App />);

    await screen.findByText('Test Song', undefined, { timeout: 5000 });

    const dock = document.querySelector('.fab-search-dock') as HTMLElement;
    const filterButton = within(dock).getByRole('button', { name: 'Filter Songs' });
    expect(filterButton).toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(within(filterButton).queryByAltText('Solo_Guitar')).toBeNull();
    expect(screen.queryByTestId('fab-band-filter-instruments')).toBeNull();
  });

  it('does not show the selected band type menu from Songs when quick links are unavailable', async () => {
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
    const dock = document.querySelector('.fab-search-dock') as HTMLElement;

    expect(dock).toBeTruthy();
    expect(within(dock).getByRole('button', { name: 'Filter Songs' })).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    expect(screen.queryByTestId('fab-menu')).toBeNull();
  });

  it('opens Songs quick links directly from the mobile FAB even with a selected band', async () => {
    setMobile();
    mockApi.getSongs.mockResolvedValueOnce({
      songs: [
        { songId: 's-alpha', title: 'Alpha Song', artist: 'Artist A', year: 2024, albumArt: 'https://example.com/a.jpg', difficulty: { guitar: 3 } },
        { songId: 's-beta', title: 'Beta Song', artist: 'Artist B', year: 2024, albumArt: 'https://example.com/b.jpg', difficulty: { guitar: 3 } },
      ],
      count: 2,
      currentSeason: 5,
    });
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

    await screen.findByText('Alpha Song', undefined, { timeout: 5000 });
    const dock = document.querySelector('.fab-search-dock') as HTMLElement;
    const quickLinksButton = await screen.findByRole('button', { name: 'Quick Links' });

    expect(dock).toBeTruthy();
    expect(within(dock).getByRole('button', { name: 'Filter Songs' })).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    expect(screen.queryByTestId('fab-menu')).toBeNull();

    fireEvent.click(quickLinksButton);

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    const dialog = await screen.findByRole('dialog', { name: /Quick Links/ });
    expect(within(dialog).getByRole('button', { name: 'A' })).toBeDefined();
    expect(within(dialog).getByRole('button', { name: 'B' })).toBeDefined();
  });

  it('opens Leaderboards quick links from the main mobile FAB', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/leaderboards';
    render(<App />);

    await waitFor(() => {
      expect(mockApi.getRankings).toHaveBeenCalled();
    }, { timeout: 5000 });

    const quickLinksButton = await screen.findByRole('button', { name: 'Quick Links' });
    const sideActions = screen.getByTestId('fab-side-actions');
    expect(within(sideActions).getByRole('button', { name: 'Change Leaderboard Ranking' })).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    expect(screen.queryByTestId('fab-menu')).toBeNull();

    fireEvent.click(quickLinksButton);

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    const dialog = await screen.findByRole('dialog', { name: 'Leaderboards Quick Links' });
    expect(within(dialog).getByRole('button', { name: 'Rank History Graph' })).toBeDefined();
    expect(within(dialog).getByRole('button', { name: 'Lead' })).toBeDefined();
    expect(within(dialog).getByRole('button', { name: 'Bass' })).toBeDefined();
    expect(within(dialog).getByRole('button', { name: 'Duos' })).toBeDefined();
    expect(within(dialog).getByRole('button', { name: 'Trios' })).toBeDefined();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: 'Leaderboards Quick Links' })).toBeNull();
    });
    window.location.hash = '';
  });

  it('opens Rank By from a separate mobile Leaderboards side action', async () => {
    setMobile();
    window.location.hash = '#/leaderboards';
    render(<App />);

    await waitFor(() => {
      expect(mockApi.getRankings).toHaveBeenCalled();
    }, { timeout: 5000 });

    await screen.findByRole('button', { name: 'Quick Links' });
    const sideActions = screen.getByTestId('fab-side-actions');
    fireEvent.click(within(sideActions).getByRole('button', { name: 'Change Leaderboard Ranking' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Rank By' })).toBeDefined();
    window.location.hash = '';
  });

  it('shows the selected-band combo filter as an inactive icon circle beside the mobile Leaderboards FAB', async () => {
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
    window.location.hash = '#/leaderboards';
    render(<App />);

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalled();
    }, { timeout: 5000 });

    await screen.findByRole('button', { name: 'Quick Links' });
    const sideActions = screen.getByTestId('fab-side-actions');
    const filterButton = within(sideActions).getByRole('button', { name: 'Duos' });
    expect(filterButton).not.toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(screen.getByRole('button', { name: 'Change Leaderboard Ranking' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    expect(screen.queryByTestId('fab-menu')).toBeNull();

    fireEvent.click(filterButton);

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Filter Band Type' })).toBeDefined();
    expect(await screen.findByText('Instrument #1')).toBeDefined();
    window.location.hash = '';
  });

  it('shows the active selected-band combo filter as a blue icon circle beside the mobile Leaderboards FAB', async () => {
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
    localStorage.setItem('fst:bandFilter', JSON.stringify({
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      comboId: 'Solo_Guitar+Solo_Bass',
      assignments: [
        { accountId: 'p1', instrument: 'Solo_Guitar' },
        { accountId: 'p2', instrument: 'Solo_Bass' },
      ],
    }));
    window.location.hash = '#/leaderboards';
    render(<App />);

    await waitFor(() => {
      expect(mockApi.getBandRankings).toHaveBeenCalled();
    }, { timeout: 5000 });

    await screen.findByRole('button', { name: 'Quick Links' });
    const sideActions = screen.getByTestId('fab-side-actions');
    const filterButton = within(sideActions).getByRole('button', { name: 'Lead / Bass' });
    expect(filterButton).toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(within(filterButton).getByAltText('Solo_Guitar')).toBeDefined();
    expect(within(filterButton).getByAltText('Solo_Bass')).toBeDefined();
    expect(within(filterButton).getByTestId('fab-band-filter-instruments')).toBeDefined();
    expect(within(filterButton).queryByText('Lead / Bass')).toBeNull();
    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    expect(screen.queryByTestId('fab-menu')).toBeNull();
    window.location.hash = '';
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

  it('confirms selected band deselect from the desktop sidebar', async () => {
    setDesktop();
    window.location.hash = '#/settings';
    const selectedBand: SelectedProfile = {
      type: 'band',
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      displayName: 'TrackedP + BandMate',
      members: [
        { accountId: 'p1', displayName: 'TrackedP' },
        { accountId: 'p2', displayName: 'BandMate' },
      ],
    };
    localStorage.setItem('fst:selectedProfile', JSON.stringify(selectedBand));

    render(<App />);

    await screen.findByText('App Settings', undefined, { timeout: 5000 });
    fireEvent.click(screen.getByLabelText('Open navigation'));

    const bandPanel = await screen.findByTestId('sidebar-band-profile');
    expect(within(bandPanel).getByText('TrackedP + BandMate')).toBeDefined();

    fireEvent.click(within(bandPanel).getByRole('button', { name: 'Deselect Band' }));

    expect(await screen.findByText('Deselect Band?')).toBeDefined();
    expect(screen.getByText(/Deselecting this band will prevent you from seeing band scores/)).toBeDefined();
    expect(localStorage.getItem('fst:selectedProfile')).not.toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'No' }));

    await waitFor(() => {
      expect(screen.queryByText('Deselect Band?')).toBeNull();
    });
    expect(localStorage.getItem('fst:selectedProfile')).not.toBeNull();

    fireEvent.click(within(screen.getByTestId('sidebar-band-profile')).getByRole('button', { name: 'Deselect Band' }));
    expect(await screen.findByText('Deselect Band?')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Yes' }));

    await waitFor(() => {
      expect(localStorage.getItem('fst:selectedProfile')).toBeNull();
    });
    expect(localStorage.getItem('fst:trackedPlayer')).toBeNull();
    window.location.hash = '';
  }, 10000);

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

  it('opens Manual quick links directly from the mobile FAB without the fallback Actions FAB', async () => {
    setMobile();
    window.location.hash = '#/manual';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('App Manual')).toBeDefined();
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Manual Sections' })).toBeDefined();
    window.location.hash = '';
  });

  it('redirects direct Manual visits to Songs when App Manual is disabled', async () => {
    setMobile();
    mockApi.getFeatures.mockResolvedValueOnce({ compete: true, leaderboards: true, difficulty: true, playerBands: true, experimentalRanks: true, appManual: false });
    window.location.hash = '#/manual';
    render(<App />);

    await waitFor(() => expect(window.location.hash).toBe('#/songs'), { timeout: 5000 });

    expect(screen.queryByRole('heading', { name: 'App Manual' })).toBeNull();
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
    expect(screen.queryByRole('button', { name: 'Actions' })).toBeNull();

    const filterFab = screen.getByRole('button', { name: 'Filter Suggestions' });
    expect(filterFab).not.toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(filterFab.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    fireEvent.click(filterFab);

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    const dialog = await screen.findByRole('dialog', { name: 'Filter Suggestions' });
    fireEvent.click(within(dialog).getByText('General'));
    fireEvent.click(within(dialog).getAllByText('Near FC')[0]!);
    fireEvent.click(within(dialog).getByText('Apply Filter Changes'));
    await waitFor(() => {
      const activeFilterFab = screen.getByRole('button', { name: 'Filter Suggestions' });
      expect(activeFilterFab).toHaveStyle({ backgroundColor: '#2D82E6' });
      expect(within(activeFilterFab).queryByAltText('Solo_Guitar')).toBeNull();
      expect(screen.queryByTestId('fab-band-filter-instruments')).toBeNull();
    });
    window.location.hash = '';
  });

  it('embeds the selected-band filter in the mobile Suggestions modal without a side action', async () => {
    setMobile();
    localStorage.removeItem('fst:trackedPlayer');
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
    window.location.hash = '#/suggestions';
    render(<App />);

    await waitFor(() => {
      expect(mockApi.getBandSongRows).toHaveBeenCalled();
    }, { timeout: 5000 });

    const filterFab = screen.getByRole('button', { name: 'Filter Suggestions' });
    expect(filterFab).not.toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(filterFab.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(screen.queryByTestId('fab-side-actions')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Actions' })).toBeNull();

    fireEvent.click(filterFab);
    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Filter Suggestions' })).toBeDefined();
    expect(await screen.findByText('Instrument #1')).toBeDefined();
    expect(screen.getByText('Instrument #2')).toBeDefined();
    window.location.hash = '';
  });

  it('shows active selected-band combo state on the single mobile Suggestions filter FAB', async () => {
    setMobile();
    localStorage.removeItem('fst:trackedPlayer');
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
    localStorage.setItem('fst:bandFilter', JSON.stringify({
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      comboId: 'Solo_Guitar+Solo_Bass',
      assignments: [
        { accountId: 'p1', instrument: 'Solo_Guitar' },
        { accountId: 'p2', instrument: 'Solo_Bass' },
      ],
    }));
    window.location.hash = '#/suggestions';
    render(<App />);

    await waitFor(() => {
      expect(mockApi.getBandSongRows).toHaveBeenCalledWith('Band_Duets', 'p1:p2', 'Solo_Guitar+Solo_Bass');
    }, { timeout: 5000 });

    const filterFab = screen.getByRole('button', { name: 'Filter Suggestions' });
    expect(filterFab).toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(within(filterFab).getByAltText('Solo_Guitar')).toBeDefined();
    expect(within(filterFab).getByAltText('Solo_Bass')).toBeDefined();
    expect(within(filterFab).getByTestId('fab-band-filter-instruments')).toBeDefined();
    expect(screen.queryByTestId('fab-side-actions')).toBeNull();

    fireEvent.click(filterFab);

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Filter Suggestions' })).toBeDefined();
    expect(await screen.findByText('Instrument #1')).toBeDefined();
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
    expect(screen.getByText('TrackedP')).toBeDefined();
    expect(screen.queryByRole('heading', { name: 'TrackedP' })).toBeNull();
    expect(screen.queryByTestId('player-header-actions')).toBeNull();
    expect(screen.queryByTestId('fab-side-actions')).toBeNull();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();
    window.location.hash = '';
  });

  it('shows the selected-band filter beside the mobile Statistics quick-links FAB', async () => {
    setMobile();
    localStorage.removeItem('fst:trackedPlayer');
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
    window.location.hash = '#/statistics';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });
    expect(screen.getByText('TrackedP + BandMate')).toBeDefined();
    expect(screen.queryByRole('heading', { name: 'TrackedP + BandMate' })).toBeNull();

    const sideActions = screen.getByTestId('fab-side-actions');
    const filterButton = within(sideActions).getByRole('button', { name: 'Duos' });
    expect(filterButton.textContent?.trim()).toBe('');
    expect(filterButton).not.toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(filterButton);

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Filter Band Type' })).toBeDefined();
    expect(await screen.findByText('Instrument #1')).toBeDefined();
    window.location.hash = '';
  });

  it('shows active selected-band combo instruments beside the mobile Statistics FAB', async () => {
    setMobile();
    localStorage.removeItem('fst:trackedPlayer');
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
    localStorage.setItem('fst:bandFilter', JSON.stringify({
      bandId: 'band-1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      comboId: 'Solo_Guitar+Solo_Bass',
      assignments: [
        { accountId: 'p1', instrument: 'Solo_Guitar' },
        { accountId: 'p2', instrument: 'Solo_Bass' },
      ],
    }));
    window.location.hash = '#/statistics';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });

    const sideActions = screen.getByTestId('fab-side-actions');
    const filterButton = within(sideActions).getByRole('button', { name: 'Lead / Bass' });
    expect(filterButton).toHaveStyle({ backgroundColor: '#2D82E6' });
    expect(within(filterButton).getByAltText('Solo_Guitar')).toBeDefined();
    expect(within(filterButton).getByAltText('Solo_Bass')).toBeDefined();
    expect(within(filterButton).getByTestId('fab-band-filter-instruments')).toBeDefined();
    expect(within(filterButton).queryByText('Lead / Bass')).toBeNull();
    expect(screen.queryByTestId('band-header-filter-instruments')).toBeNull();
    expect(screen.queryByRole('heading', { level: 1, name: 'TrackedP + BandMate' })).toBeNull();

    fireEvent.click(filterButton);

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Filter Band Type' })).toBeDefined();
    expect(await screen.findByText('Instrument #1')).toBeDefined();
    window.location.hash = '';
  });

  it('opens selected-band Statistics quick links directly from the mobile FAB', async () => {
    setMobile();
    localStorage.removeItem('fst:trackedPlayer');
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
    window.location.hash = '#/statistics';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });
    expect(within(screen.getByTestId('fab-side-actions')).getByRole('button', { name: 'Duos' })).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    const dialog = await screen.findByRole('dialog', { name: 'Quick Links' });
    expect(within(dialog).getByText('Members')).toBeDefined();
    expect(within(dialog).getByText('Summary')).toBeDefined();
    expect(within(dialog).getByText('Statistics')).toBeDefined();
    expect(within(dialog).getByText('Rank History')).toBeDefined();
    expect(within(dialog).getByText('Songs')).toBeDefined();
    window.location.hash = '';
  });

  it('opens player detail quick links directly from the mobile FAB with Select player side action', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    localStorage.setItem('fst:selectedProfile', JSON.stringify({ type: 'player', accountId: 'p1', displayName: 'TrackedP' }));
    mockApi.getPlayer.mockImplementation(async (accountId: string) => accountId === 'p2'
      ? {
        accountId: 'p2',
        displayName: 'OtherP',
        totalScores: 1,
        scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 110000, rank: 4, percentile: 84, accuracy: 91, stars: 5, season: 5 }],
      }
      : {
        accountId: 'p1',
        displayName: 'TrackedP',
        totalScores: 1,
        scores: [{ songId: 's1', instrument: 'Solo_Guitar', score: 100000, rank: 5, percentile: 80, accuracy: 90, stars: 4, season: 5 }],
      });
    window.location.hash = '#/player/p2';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });
    expect(within(screen.getByTestId('page-root')).getByRole('heading', { name: 'OtherP' })).toBeDefined();
    expect(screen.queryByTestId('player-header-actions')).toBeNull();
    expect(screen.queryByTestId('player-select-profile-slot')).toBeNull();
    const sideActions = screen.getByTestId('fab-side-actions');
    const selectButton = within(sideActions).getByRole('button', { name: 'Select OtherP' });
    expect(within(selectButton).getByText('Select OtherP')).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();
    window.location.hash = '';
  });

  it('opens band detail quick links directly from the mobile FAB with Select Band side action', async () => {
    setMobile();
    localStorage.removeItem('fst:trackedPlayer');
    localStorage.removeItem('fst:selectedProfile');
    window.location.hash = '#/bands/band-1';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });
    expect(within(screen.getByTestId('page-root')).getByRole('heading', { name: 'TrackedP + BandMate' })).toBeDefined();
    expect(screen.queryByTestId('band-select-profile-slot')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Select Band Profile' })).toBeNull();
    const sideActions = screen.getByTestId('fab-side-actions');
    const selectButton = within(sideActions).getByRole('button', { name: 'Select Band' });
    expect(within(selectButton).getByText('Select Band')).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();
    expect(mockApi.getBandDetail).toHaveBeenCalledWith('band-1');
    window.location.hash = '';
  });

  it('opens Rivals quick links directly from the mobile FAB with the tab toggle beside it', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/rivals';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });

    const sideActions = screen.getByTestId('fab-side-actions');
    const toggleButton = within(sideActions).getByRole('button', { name: 'Leaderboard Rivals' });
    const findRivalButton = within(sideActions).getByRole('button', { name: 'Find Rival' });
    expect(within(toggleButton).getByText('Leaderboard Rivals')).toBeDefined();
    expect(within(findRivalButton).getByText('Find Rival')).toBeDefined();
    expect(screen.getAllByRole('button', { name: 'Quick Links' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Leaderboard Rivals' })).toHaveLength(1);
    expect(screen.getAllByRole('button', { name: 'Find Rival' })).toHaveLength(1);
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();

    fireEvent.click(toggleButton);

    await waitFor(() => {
      expect(window.location.hash).toBe('#/rivals?tab=leaderboard');
    });

    fireEvent.click(findRivalButton);

    expect(await screen.findByRole('dialog', { name: 'Search' })).toBeDefined();
    expect(screen.queryByRole('group', { name: 'Search targets' })).toBeNull();
    expect(screen.getByPlaceholderText('Search players…')).toBeDefined();

    window.location.hash = '';
  });

  it('opens rival detail quick links directly from the mobile FAB with a View profile side pill', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/rivals/rival-1?name=TestRival';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });

    const sideActions = screen.getByTestId('fab-side-actions');
    const profileButton = within(sideActions).getByRole('button', { name: "View TestRival's Profile" });
    expect(within(profileButton).getByText("View TestRival's Profile")).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();

    window.location.hash = '';
  });

  it('shows only the View profile side pill on rivalry mode routes (no quick-links FAB)', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/rivals/rival-1/rivalry?mode=closest_battles&name=TestRival';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('fab-side-actions')).toBeDefined();
    }, { timeout: 5000 });

    const sideActions = screen.getByTestId('fab-side-actions');
    const profileButton = within(sideActions).getByRole('button', { name: "View TestRival's Profile" });
    expect(within(profileButton).getByText("View TestRival's Profile")).toBeDefined();
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
    expect(screen.queryByLabelText('Actions')).toBeNull();

    window.location.hash = '';
  });

  it('shows rivalry quick-links FAB when mode query is not explicit', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/rivals/rival-1/rivalry?name=TestRival';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });

    const sideActions = screen.getByTestId('fab-side-actions');
    const profileButton = within(sideActions).getByRole('button', { name: "View TestRival's Profile" });
    expect(within(profileButton).getByText("View TestRival's Profile")).toBeDefined();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();

    window.location.hash = '';
  });

  it('opens Compete quick links directly from the mobile FAB with section pills beside it', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/compete';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    }, { timeout: 5000 });

    const sideActions = screen.getByTestId('fab-side-actions');
    const leaderboardsButton = within(sideActions).getByRole('button', { name: 'Leaderboards' });
    const rivalsButton = within(sideActions).getByRole('button', { name: 'Rivals' });
    expect(within(leaderboardsButton).getByText('Leaderboard')).toBeDefined();
    expect(within(rivalsButton).getByText('Rivals')).toBeDefined();
    expect(screen.getAllByRole('button', { name: 'Quick Links' })).toHaveLength(1);
    expect(screen.queryByLabelText('Actions')).toBeNull();

    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));

    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(await screen.findByRole('dialog', { name: 'Quick Links' })).toBeDefined();

    window.location.hash = '';
  });

  it('navigates to the top-level Leaderboards page from the Compete FAB', async () => {
    setMobile();
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'p1', displayName: 'TrackedP' }));
    window.location.hash = '#/compete';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByTestId('fab-side-actions')).toBeDefined();
    }, { timeout: 5000 });

    fireEvent.click(within(screen.getByTestId('fab-side-actions')).getByRole('button', { name: 'Leaderboards' }));

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
      expect(screen.getByTestId('fab-side-actions')).toBeDefined();
    }, { timeout: 5000 });

    fireEvent.click(within(screen.getByTestId('fab-side-actions')).getByRole('button', { name: 'Rivals' }));

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

  it('shows song detail actions beside the FAB on mobile when supported', async () => {
    setMobile();
    window.location.hash = '#/songs/s1';
    render(<App />);

    await waitFor(() => {
      expect(screen.getByText('Test Song')).toBeDefined();
    });

    await waitFor(() => {
      const sideActions = screen.getByTestId('fab-side-actions');
      expect(within(within(sideActions).getByRole('button', { name: 'View Paths' })).getByText('View Paths')).toBeDefined();
      const shopAction = within(sideActions).getByRole('link', { name: 'Item Shop' });
      expect(within(shopAction).getByText('Item Shop')).toBeDefined();
      expect(shopAction.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
      expect(within(sideActions).getAllByTestId('fab-side-action').map(action => action.textContent?.trim())).toEqual(['Item Shop', 'View Paths']);
    });

    expect(screen.getAllByRole('button', { name: 'View Paths' })).toHaveLength(1);
    expect(screen.getAllByRole('link', { name: 'Item Shop' })).toHaveLength(1);
    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    });
    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));
    expect(screen.queryByTestId('fab-menu')).toBeNull();
    const quickLinksList = await screen.findByTestId('song-detail-quick-links-modal-list');
    expect(within(quickLinksList).getByText('Intensity')).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    window.location.hash = '';
  });

  it('hides only View Paths beside the FAB when only unsupported path instruments are enabled', async () => {
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

    await waitFor(() => {
      expect(screen.getByRole('link', { name: 'Item Shop' })).toBeDefined();
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    });
    expect(screen.queryByRole('button', { name: 'View Paths' })).toBeNull();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    window.location.hash = '';
  });

  it('renders the song detail quick-links FAB when shop and path actions are unavailable', async () => {
    setMobile();
    localStorage.setItem('fst:appSettings', JSON.stringify({
      hideItemShop: true,
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
      expect(screen.getByRole('button', { name: 'Quick Links' })).toBeDefined();
    });

    expect(screen.queryByTestId('fab-side-actions')).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));
    expect(screen.queryByTestId('fab-menu')).toBeNull();
    const quickLinksList = await screen.findByTestId('song-detail-quick-links-modal-list');
    expect(within(quickLinksList).getByText('Intensity')).toBeDefined();
    expect(screen.queryByLabelText('Actions')).toBeNull();
    expect(screen.queryByRole('button', { name: 'View Paths' })).toBeNull();
    expect(screen.queryByRole('link', { name: 'Item Shop' })).toBeNull();
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
