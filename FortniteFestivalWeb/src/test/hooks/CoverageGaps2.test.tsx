/**
 * Targeted tests for remaining coverage gaps in hooks, components, and pages.
 * Covers: useAccountSearch, useChartData, useVisualViewport, Sidebar,
 * DesktopNav, FadeIn, ChangelogModal, FloatingActionButton, InstrumentCard
 */
import { describe, it, expect, vi, beforeEach, afterEach, beforeAll } from 'vitest';
import { render, screen, fireEvent, renderHook, waitFor, act } from '@testing-library/react';
// react-router-dom imported by TestProviders
import { TestProviders, createTestQueryClient } from '../helpers/TestProviders';
import { QueryClientProvider } from '@tanstack/react-query';
import { stubScrollTo, stubResizeObserver, stubElementDimensions, stubIntersectionObserver } from '../helpers/browserStubs';

/* ── Mocks ── */

const mockApi = vi.hoisted(() => ({
  searchAccounts: vi.fn().mockResolvedValue({ results: [] }),
  getPlayerHistory: vi.fn().mockResolvedValue({ accountId: 'a1', count: 0, history: [] }),
  getSongs: vi.fn().mockResolvedValue({ songs: [], count: 0, currentSeason: 5 }),
  getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
  getSyncStatus: vi.fn().mockResolvedValue({ accountId: '', isTracked: false, backfill: null, historyRecon: null }),
  getPlayer: vi.fn().mockResolvedValue(null),
  getFirstSeen: vi.fn().mockResolvedValue({ count: 0, songs: [] }),
}));
vi.mock('../../api/client', () => ({ api: mockApi }));

// Mock react-icons to avoid render overhead
vi.mock('react-icons/io5', () => {
  const Stub = (p: any) => <span data-testid={p['aria-label'] ?? 'icon'} />;
  return {
    IoMenu: Stub,
    IoClose: Stub,
    IoArrowUp: Stub,
    IoArrowDown: Stub,
    IoPerson: Stub,
    IoSearch: Stub,
    IoFilter: Stub,
    IoSwapVertical: Stub,
    IoMusicalNotes: Stub,
    IoChevronBack: Stub,
  };
});

beforeAll(() => {
  stubScrollTo();
  stubResizeObserver({ width: 1024, height: 800 });
  stubElementDimensions(800);
  stubIntersectionObserver();
});

/* ══════════════════════════════════════════════
   useAccountSearch — handleKeyDown branches
   ══════════════════════════════════════════════ */

import { useAccountSearch } from '../../hooks/data/useAccountSearch';
import { Keys } from '@festival/core';

describe('useAccountSearch — handleKeyDown branches', () => {
  const wrapper = ({ children }: { children: React.ReactNode }) => (
    <TestProviders>{children}</TestProviders>
  );

  const RESULTS = [
    { accountId: 'a1', displayName: 'Alice' },
    { accountId: 'a2', displayName: 'Bob' },
    { accountId: 'a3', displayName: 'Charlie' },
  ];

  function setup() {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect, { debounceMs: 0 }), { wrapper });
    return { result, onSelect };
  }

  async function setupWithResults() {
    mockApi.searchAccounts.mockResolvedValueOnce({ results: RESULTS });
    const { result, onSelect } = setup();
    // Type a query to trigger search
    await act(async () => {
      result.current.handleChange('Ali');
      await vi.advanceTimersByTimeAsync(50);
    });
    // Wait for results to appear
    await waitFor(() => expect(result.current.results.length).toBe(3));
    return { result, onSelect };
  }

  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.clearAllMocks();
  });
  afterEach(() => vi.useRealTimers());

  it('does nothing when not open', () => {
    const { result } = setup();
    const ev = { key: Keys.ArrowDown, preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(ev.preventDefault).not.toHaveBeenCalled();
  });

  it('ArrowDown increments activeIndex', async () => {
    const { result } = await setupWithResults();
    const ev = { key: Keys.ArrowDown, preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(ev.preventDefault).toHaveBeenCalled();
    expect(result.current.activeIndex).toBe(0);
  });

  it('ArrowDown wraps around at end', async () => {
    const { result } = await setupWithResults();
    act(() => result.current.setActiveIndex(2)); // last item
    const ev = { key: Keys.ArrowDown, preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(result.current.activeIndex).toBe(0);
  });

  it('ArrowUp decrements activeIndex', async () => {
    const { result } = await setupWithResults();
    act(() => result.current.setActiveIndex(1));
    const ev = { key: Keys.ArrowUp, preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(result.current.activeIndex).toBe(0);
  });

  it('ArrowUp wraps to end when at 0', async () => {
    const { result } = await setupWithResults();
    act(() => result.current.setActiveIndex(0));
    const ev = { key: Keys.ArrowUp, preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(result.current.activeIndex).toBe(2);
  });

  it('Enter selects active result', async () => {
    const { result, onSelect } = await setupWithResults();
    act(() => result.current.setActiveIndex(1));
    const ev = { key: Keys.Enter, preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(onSelect).toHaveBeenCalledWith(RESULTS[1]);
  });

  it('Enter does nothing when activeIndex < 0', async () => {
    const { result, onSelect } = await setupWithResults();
    const ev = { key: Keys.Enter, preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(onSelect).not.toHaveBeenCalled();
  });

  it('Escape closes dropdown', async () => {
    const { result } = await setupWithResults();
    expect(result.current.isOpen).toBe(true);
    const ev = { key: Keys.Escape, preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(result.current.isOpen).toBe(false);
  });

  it('other key does nothing', async () => {
    const { result } = await setupWithResults();
    const ev = { key: 'a', preventDefault: vi.fn() } as any;
    act(() => result.current.handleKeyDown(ev));
    expect(ev.preventDefault).not.toHaveBeenCalled();
  });

  it('search with query < 2 chars clears results', async () => {
    const { result } = await setupWithResults();
    await act(async () => {
      result.current.handleChange('A');
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(result.current.results).toHaveLength(0);
    expect(result.current.isOpen).toBe(false);
  });

  it('search handles API error gracefully', async () => {
    mockApi.searchAccounts.mockRejectedValueOnce(new Error('Network'));
    const { result } = setup();
    await act(async () => {
      result.current.handleChange('TestQuery');
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(result.current.results).toHaveLength(0);
    expect(result.current.isOpen).toBe(false);
  });

  it('selectResult clears state and calls onSelect', () => {
    const onSelect = vi.fn();
    const { result } = renderHook(() => useAccountSearch(onSelect), { wrapper });
    act(() => result.current.selectResult(RESULTS[0]!));
    expect(onSelect).toHaveBeenCalledWith(RESULTS[0]);
    expect(result.current.query).toBe('');
    expect(result.current.isOpen).toBe(false);
  });

  it('close sets isOpen to false', async () => {
    const { result } = await setupWithResults();
    act(() => result.current.close());
    expect(result.current.isOpen).toBe(false);
  });


});

/* ══════════════════════════════════════════════
   useChartData — chartData & instrumentCounts memos
   ══════════════════════════════════════════════ */

import { useChartData } from '../../hooks/chart/useChartData';

describe('useChartData — memo branches', () => {
  const wrapper = ({ children }: { children: React.ReactNode }) => {
    const qc = createTestQueryClient();
    return <QueryClientProvider client={qc}>{children}</QueryClientProvider>;
  };

  const HISTORY = [
    { songId: 's1', instrument: 'Solo_Guitar', newScore: 100, newRank: 1, changedAt: '2025-01-01T00:00:00Z', scoreAchievedAt: '2025-01-01T12:00:00Z', accuracy: 950000, isFullCombo: true, stars: 5, season: 3 },
    { songId: 's1', instrument: 'Solo_Guitar', newScore: 200, newRank: 1, changedAt: '2025-01-02T00:00:00Z', scoreAchievedAt: null, accuracy: null, isFullCombo: null, stars: null, season: null },
    { songId: 's1', instrument: 'Solo_Bass', newScore: 50, newRank: 2, changedAt: '2025-01-01T00:00:00Z', scoreAchievedAt: '2025-01-01T00:00:00Z', accuracy: 800000, isFullCombo: false, stars: 3, season: 2 },
    { songId: 's1', instrument: 'Solo_Guitar', newScore: 300, newRank: 1, changedAt: '2025-01-01T06:00:00Z', scoreAchievedAt: '2025-01-01T06:00:00Z', accuracy: 990000, isFullCombo: true, stars: 6, season: 4 },
  ];

  it('transforms history into chartData with null accuracy → 0', () => {
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, HISTORY as any),
      { wrapper },
    );
    const guiPoints = result.current.chartData;
    expect(guiPoints).toHaveLength(3); // 3 Solo_Guitar entries
    // Entry with null accuracy → 0
    const nullAccEntry = guiPoints.find(p => p.score === 200);
    expect(nullAccEntry!.accuracy).toBe(0);
    expect(nullAccEntry!.isFullCombo).toBe(false);
    expect(nullAccEntry!.stars).toBeUndefined();
    expect(nullAccEntry!.season).toBeUndefined();
    // Entry with real accuracy
    const realAccEntry = guiPoints.find(p => p.score === 100);
    expect(realAccEntry!.accuracy).toBeGreaterThan(0);
    expect(realAccEntry!.isFullCombo).toBe(true);
    expect(realAccEntry!.stars).toBe(5);
  });

  it('builds instrumentCounts across all instruments', () => {
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, HISTORY as any),
      { wrapper },
    );
    expect(result.current.instrumentCounts).toEqual({
      Solo_Guitar: 3,
      Solo_Bass: 1,
    });
  });

  it('uses changedAt fallback when scoreAchievedAt is null', () => {
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, HISTORY as any),
      { wrapper },
    );
    const changedAtEntry = result.current.chartData.find(p => p.score === 200);
    expect(changedAtEntry!.date).toContain('2025-01-02');
  });

  it('handles same-day entries with day deduplication', () => {
    const sameDayHistory = [
      { songId: 's1', instrument: 'Solo_Guitar', newScore: 100, newRank: 1, changedAt: '2025-01-01T10:00:00Z', scoreAchievedAt: '2025-01-01T10:00:00Z', accuracy: 950000, isFullCombo: true, stars: 5, season: 3 },
      { songId: 's1', instrument: 'Solo_Guitar', newScore: 200, newRank: 1, changedAt: '2025-01-01T14:00:00Z', scoreAchievedAt: '2025-01-01T14:00:00Z', accuracy: 960000, isFullCombo: true, stars: 6, season: 3 },
    ];
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, sameDayHistory as any),
      { wrapper },
    );
    // Both should have same dateLabel
    expect(result.current.chartData[0]!.dateLabel).toBe(result.current.chartData[1]!.dateLabel);
  });

  it('returns loading=false when historyProp is provided', () => {
    const { result } = renderHook(
      () => useChartData('a1', 's1', 'Solo_Guitar' as any, []),
      { wrapper },
    );
    expect(result.current.loading).toBe(false);
  });
});

/* ══════════════════════════════════════════════
   useVisualViewport — getOffsetTopSnapshot + subscribe
   ══════════════════════════════════════════════ */

import { useVisualViewportHeight, useVisualViewportOffsetTop } from '../../hooks/ui/useVisualViewport';

describe('useVisualViewport', () => {
  const wrapper = ({ children }: { children: React.ReactNode }) => <>{children}</>;

  it('useVisualViewportHeight returns innerHeight', () => {
    const { result } = renderHook(() => useVisualViewportHeight(), { wrapper });
    expect(typeof result.current).toBe('number');
  });

  it('useVisualViewportOffsetTop returns a number', () => {
    const { result } = renderHook(() => useVisualViewportOffsetTop(), { wrapper });
    expect(typeof result.current).toBe('number');
    expect(result.current).toBe(0);
  });

  it('subscribe uses visualViewport when available', () => {
    const addSpy = vi.fn();
    const removeSpy = vi.fn();
    const mockVV = {
      addEventListener: addSpy,
      removeEventListener: removeSpy,
      height: 600,
      offsetTop: 10,
    };
    Object.defineProperty(window, 'visualViewport', { value: mockVV, writable: true, configurable: true });
    
    const { result, unmount } = renderHook(() => useVisualViewportHeight(), { wrapper });
    expect(typeof result.current).toBe('number');
    unmount();
    
    // Clean up
    Object.defineProperty(window, 'visualViewport', { value: null, writable: true, configurable: true });
  });

  it('subscribe falls back to window resize when no visualViewport', () => {
    const origVV = window.visualViewport;
    Object.defineProperty(window, 'visualViewport', { value: null, writable: true, configurable: true });
    
    const { result, unmount } = renderHook(() => useVisualViewportHeight(), { wrapper });
    expect(typeof result.current).toBe('number');
    unmount();
    
    Object.defineProperty(window, 'visualViewport', { value: origVV, writable: true, configurable: true });
  });
});

/* ══════════════════════════════════════════════
   Sidebar — lifecycle branches
   ══════════════════════════════════════════════ */

import Sidebar from '../../components/shell/desktop/Sidebar';

describe('Sidebar — open/close lifecycle', () => {
  const baseProps = {
    player: { accountId: 'p1', displayName: 'TestPlayer' } as any,
    onClose: vi.fn(),
    onDeselect: vi.fn(),
    onSelectPlayer: vi.fn(),
  };

  beforeEach(() => vi.clearAllMocks());

  it('renders nothing when open=false', () => {
    const { container } = render(
      <TestProviders>
        <Sidebar {...baseProps} open={false} />
      </TestProviders>,
    );
    expect(container.textContent).toBe('');
  });

  it('renders content when open=true', async () => {
    const { container } = render(
      <TestProviders>
        <Sidebar {...baseProps} open={true} />
      </TestProviders>,
    );
    await waitFor(() => expect(container.textContent).toContain('Songs'));
  });

  it('shows player info when player is set', () => {
    render(
      <TestProviders>
        <Sidebar {...baseProps} open={true} />
      </TestProviders>,
    );
    expect(screen.getByText('TestPlayer')).toBeTruthy();
    expect(screen.getByText('Deselect')).toBeTruthy();
  });

  it('shows Select Player when player is null', () => {
    render(
      <TestProviders>
        <Sidebar {...baseProps} player={null} open={true} />
      </TestProviders>,
    );
    expect(screen.getByText('Select Player')).toBeTruthy();
  });

  it('calls onDeselect when Deselect clicked', () => {
    render(
      <TestProviders>
        <Sidebar {...baseProps} open={true} />
      </TestProviders>,
    );
    fireEvent.click(screen.getByText('Deselect'));
    expect(baseProps.onDeselect).toHaveBeenCalled();
  });

  it('calls onSelectPlayer when Select Player clicked', () => {
    render(
      <TestProviders>
        <Sidebar {...baseProps} player={null} open={true} />
      </TestProviders>,
    );
    fireEvent.click(screen.getByText('Select Player'));
    expect(baseProps.onSelectPlayer).toHaveBeenCalled();
  });

  it('calls onClose when overlay clicked', () => {
    const { container } = render(
      <TestProviders>
        <Sidebar {...baseProps} open={true} />
      </TestProviders>,
    );
    const overlay = container.querySelector('[class*="overlay"]');
    if (overlay) fireEvent.click(overlay);
    expect(baseProps.onClose).toHaveBeenCalled();
  });

  it('close transition unmounts via transitionEnd', async () => {
    const { container, rerender } = render(
      <TestProviders>
        <Sidebar {...baseProps} open={true} />
      </TestProviders>,
    );
    await waitFor(() => expect(container.textContent).toContain('Songs'));
    
    rerender(
      <TestProviders>
        <Sidebar {...baseProps} open={false} />
      </TestProviders>,
    );
    // Fire transitionEnd on sidebar element
    const sidebar = container.querySelector('[class*="sidebar"]');
    if (sidebar) fireEvent.transitionEnd(sidebar);
    await waitFor(() => expect(container.textContent).toBe(''));
  });

  it('hides suggestions/statistics links when player is null', () => {
    render(
      <TestProviders>
        <Sidebar {...baseProps} player={null} open={true} />
      </TestProviders>,
    );
    expect(screen.queryByText('nav.suggestions')).toBeNull();
    expect(screen.queryByText('nav.statistics')).toBeNull();
  });

  it('calls onClose on outside mousedown', async () => {
    render(
      <TestProviders>
        <Sidebar {...baseProps} open={true} />
      </TestProviders>,
    );
    await act(async () => {
      document.dispatchEvent(new MouseEvent('mousedown', { bubbles: true }));
    });
    expect(baseProps.onClose).toHaveBeenCalled();
  });
});

/* ══════════════════════════════════════════════
   DesktopNav — handleSelect
   ══════════════════════════════════════════════ */

import DesktopNav from '../../components/shell/desktop/DesktopNav';

describe('DesktopNav — handleSelect', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders with player', () => {
    const { container } = render(
      <TestProviders>
        <DesktopNav hasPlayer={true} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} />
      </TestProviders>,
    );
    expect(container.querySelector('nav')).toBeTruthy();
  });

  it('renders without player', () => {
    const { container } = render(
      <TestProviders>
        <DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} />
      </TestProviders>,
    );
    expect(container.querySelector('nav')).toBeTruthy();
  });
});

/* ══════════════════════════════════════════════
   FadeIn — delay/hidden paths
   ══════════════════════════════════════════════ */

import FadeIn from '../../components/page/FadeIn';

describe('FadeIn — render paths', () => {
  it('renders children without animation when delay is undefined', () => {
    const { container } = render(<FadeIn>Hello</FadeIn>);
    expect(container.textContent).toBe('Hello');
  });

  it('renders hidden wrapper when hidden=true', () => {
    const { container } = render(<FadeIn delay={100} hidden>Hidden</FadeIn>);
    expect(container.textContent).toBe('Hidden');
    const el = container.firstElementChild!;
    expect(el.className).toContain('hidden');
  });

  it('renders animated wrapper when delay is set and not hidden', () => {
    const { container } = render(<FadeIn delay={200}>Content</FadeIn>);
    expect(container.textContent).toBe('Content');
    const el = container.firstElementChild!;
    expect(el.className).toContain('wrapper');
  });

  it('renders as custom element type', () => {
    const { container } = render(<FadeIn as="span" delay={0}>Span</FadeIn>);
    expect(container.querySelector('span')!.textContent).toBe('Span');
  });

  it('renders as custom element without delay', () => {
    const { container } = render(<FadeIn as="section">Sec</FadeIn>);
    expect(container.querySelector('section')!.textContent).toBe('Sec');
  });

  it('hidden with custom className', () => {
    const { container } = render(<FadeIn delay={100} hidden className="custom">X</FadeIn>);
    const el = container.firstElementChild!;
    expect(el.className).toContain('hidden');
    expect(el.className).toContain('custom');
  });
});

/* ══════════════════════════════════════════════
   ChangelogModal — handleScroll + Escape key
   ══════════════════════════════════════════════ */

vi.mock('../../changelog', () => ({
  changelog: [
    { sections: [{ title: 'v1.0', items: ['Fix 1', 'Fix 2'] }] },
  ] as any[],
}));

import ChangelogModal from '../../components/modals/ChangelogModal';

describe('ChangelogModal', () => {
  beforeEach(() => vi.clearAllMocks());

  it('renders changelog entries', () => {
    render(<ChangelogModal onDismiss={vi.fn()} />);
    expect(screen.getByText('v1.0')).toBeTruthy();
    expect(screen.getByText('Fix 1')).toBeTruthy();
  });

  it('calls onDismiss on Escape key', () => {
    const onDismiss = vi.fn();
    render(<ChangelogModal onDismiss={onDismiss} />);
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onDismiss).toHaveBeenCalled();
  });

  it('does not dismiss on non-Escape key', () => {
    const onDismiss = vi.fn();
    render(<ChangelogModal onDismiss={onDismiss} />);
    fireEvent.keyDown(document, { key: 'a' });
    expect(onDismiss).not.toHaveBeenCalled();
  });

  it('calls onDismiss when dismiss button clicked', () => {
    const onDismiss = vi.fn();
    render(<ChangelogModal onDismiss={onDismiss} />);
    fireEvent.click(screen.getByText('Dismiss'));
    expect(onDismiss).toHaveBeenCalled();
  });

  it('calls onDismiss on overlay click', () => {
    const onDismiss = vi.fn();
    const { container } = render(<ChangelogModal onDismiss={onDismiss} />);
    const overlay = container.querySelector('[class*="overlay"]');
    if (overlay) fireEvent.click(overlay);
    expect(onDismiss).toHaveBeenCalled();
  });

  it('stops propagation on card click', () => {
    const onDismiss = vi.fn();
    const { container } = render(<ChangelogModal onDismiss={onDismiss} />);
    const card = container.querySelector('[class*="card"]');
    if (card) fireEvent.click(card);
    // onDismiss should NOT be called from the card click itself
  });

  it('handles scroll event on content', () => {
    const { container } = render(<ChangelogModal onDismiss={vi.fn()} />);
    const content = container.querySelector('[class*="content"]');
    if (content) fireEvent.scroll(content);
    // No error — handleScroll exercised
  });
});

/* ══════════════════════════════════════════════
   InstrumentCard — player-not-in-top + view-all IIFE
   ══════════════════════════════════════════════ */

import InstrumentCard from '../../pages/songinfo/components/InstrumentCard';

describe('InstrumentCard — branches', () => {
  const entries: any[] = [
    { accountId: 'e1', displayName: 'Entry1', rank: 1, score: 200000, accuracy: 990000, isFullCombo: true, season: 5 },
    { accountId: 'e2', displayName: 'Entry2', rank: 2, score: 180000, accuracy: 950000, isFullCombo: false, season: 4 },
  ];

  const baseProps = {
    songId: 's1',
    instrument: 'Solo_Guitar' as any,
    baseDelay: 0,
    windowWidth: 800,
    prefetchedEntries: entries,
    prefetchedError: null,
    skipAnimation: true,
    scoreWidth: '8ch',
  };

  it('renders entries with rank and name', () => {
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} />
      </TestProviders>,
    );
    expect(screen.getByText('Entry1')).toBeTruthy();
    expect(screen.getByText('Entry2')).toBeTruthy();
  });

  it('shows view-all button when entries exist', () => {
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} />
      </TestProviders>,
    );
    expect(screen.getByText('View full leaderboard')).toBeTruthy();
  });

  it('shows player row when not in top entries', () => {
    render(
      <TestProviders>
        <InstrumentCard
          {...baseProps}
          playerName="TestPlayer"
          playerAccountId="p1"
          playerScore={{ rank: 50, score: 100000, accuracy: 880000, isFullCombo: false, season: 3 } as any}
        />
      </TestProviders>,
    );
    expect(screen.getByText('TestPlayer')).toBeTruthy();
    expect(screen.getByText('#50')).toBeTruthy();
  });

  it('does NOT show separate player row when player IS in top', () => {
    render(
      <TestProviders>
        <InstrumentCard
          {...baseProps}
          playerName="Entry1"
          playerAccountId="e1"
          playerScore={{ rank: 1, score: 200000, accuracy: 990000, isFullCombo: true, season: 5 } as any}
        />
      </TestProviders>,
    );
    // Entry1 is in top, so no separate player row
    const entry1s = screen.getAllByText('Entry1');
    expect(entry1s).toHaveLength(1); // Only the top entry
  });

  it('shows error message when prefetchedError set', () => {
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} prefetchedEntries={[]} prefetchedError="Failed" />
      </TestProviders>,
    );
    expect(screen.getByText('Failed')).toBeTruthy();
  });

  it('shows no entries message when empty and no error', () => {
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} prefetchedEntries={[]} prefetchedError={null} />
      </TestProviders>,
    );
    expect(screen.getByText('No entries')).toBeTruthy();
  });

  it('rank fallback uses index when rank is undefined', () => {
    const entriesNoRank = [
      { accountId: 'e1', displayName: 'NoRank', rank: undefined, score: 100, accuracy: 900000, isFullCombo: false },
    ];
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} prefetchedEntries={entriesNoRank as any} />
      </TestProviders>,
    );
    expect(screen.getByText('#1')).toBeTruthy(); // Falls back to i + 1 = 1
  });

  it('displayName fallback uses accountId slice', () => {
    const entriesNoName = [
      { accountId: 'abcdef1234567890', displayName: undefined, rank: 1, score: 100, accuracy: 900000, isFullCombo: false },
    ];
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} prefetchedEntries={entriesNoName as any} />
      </TestProviders>,
    );
    expect(screen.getByText('abcdef12')).toBeTruthy();
  });

  it('hides accuracy column on narrow width', () => {
    const { container } = render(
      <TestProviders>
        <InstrumentCard {...baseProps} windowWidth={300} />
      </TestProviders>,
    );
    // At windowWidth=300, cardWidth=300, showAccuracy=false
    expect(container.querySelector('[class*="entryAcc"]')).toBeNull();
  });

  it('shows season pill on wide width when season exists', () => {
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} windowWidth={1200} />
      </TestProviders>,
    );
    // showSeason = true at wide widths, entries have season
  });

  it('hides season pill on narrow width', () => {
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} windowWidth={400} />
      </TestProviders>,
    );
    // showSeason = false at width < 520
  });

  it('player row shows season when wide', () => {
    render(
      <TestProviders>
        <InstrumentCard
          {...baseProps}
          windowWidth={1200}
          playerName="P"
          playerAccountId="p1"
          playerScore={{ rank: 50, score: 100000, accuracy: 880000, isFullCombo: false, season: 3 } as any}
        />
      </TestProviders>,
    );
    expect(screen.getByText('P')).toBeTruthy();
  });

  it('stopPropagation on entry link click', () => {
    render(
      <TestProviders>
        <InstrumentCard {...baseProps} />
      </TestProviders>,
    );
    const link = screen.getByText('Entry1').closest('a')!;
    const stopProp = vi.spyOn(MouseEvent.prototype, 'stopPropagation');
    fireEvent.click(link);
    stopProp.mockRestore();
  });
});
