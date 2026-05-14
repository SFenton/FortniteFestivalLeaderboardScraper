import { afterEach, describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen, fireEvent, within } from '@testing-library/react';
import { Font, Layout } from '@festival/theme';
import FloatingActionButton, { type ActionItem } from '../../../../src/components/shell/fab/FloatingActionButton';
import { SONGS_FAB_KEYBOARD_INSET_VAR, SONGS_FAB_KEYBOARD_OCCLUDED_BOTTOM_VAR } from '../../../../src/constants/keyboardLayoutVars';
import { TestProviders } from '../../../helpers/TestProviders';

vi.mock('@festival/ui-utils', async () => {
  const actual = await vi.importActual<typeof import('@festival/ui-utils')>('@festival/ui-utils');
  return {
    ...actual,
    IS_IOS: true,
    IS_ANDROID: false,
    IS_PWA: true,
    IS_MOBILE_DEVICE: true,
  };
});

class MockVisualViewport extends EventTarget {
  height = 844;
  offsetTop = 0;

  set(height: number, offsetTop: number) {
    this.height = height;
    this.offsetTop = offsetTop;
    this.dispatchEvent(new Event('resize'));
    this.dispatchEvent(new Event('scroll'));
  }
}

let visualViewport: MockVisualViewport;

function rectWithWidth(width: number, height = 56): DOMRect {
  return {
    x: 0,
    y: 0,
    top: 0,
    left: 0,
    right: width,
    bottom: height,
    width,
    height,
    toJSON: () => ({}),
  } as DOMRect;
}

function mockDockLabelMeasurements(stageWidth: number, controlWidths: number[]) {
  const originalGetBoundingClientRect = HTMLElement.prototype.getBoundingClientRect;
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(function getBoundingClientRectMock(this: HTMLElement) {
    if (this.getAttribute('data-testid') === 'fab-dock-stage') return rectWithWidth(stageWidth);
    if (this.getAttribute('data-dock-label-measure') === 'control') {
      const index = Number(this.getAttribute('data-dock-label-index') ?? 0);
      return rectWithWidth(controlWidths[index] ?? 56);
    }
    return originalGetBoundingClientRect.call(this);
  });
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.useFakeTimers({ shouldAdvanceTime: true });
  document.documentElement.removeAttribute('style');
  document.body.removeAttribute('style');
  visualViewport = new MockVisualViewport();
  Object.defineProperty(window, 'visualViewport', { configurable: true, value: visualViewport });
  Object.defineProperty(window, 'innerHeight', { configurable: true, value: 844 });
  Object.defineProperty(window, 'scrollX', { configurable: true, value: 0 });
  Object.defineProperty(window, 'scrollY', { configurable: true, value: 0 });
  Object.defineProperty(window, 'scrollTo', { configurable: true, value: vi.fn() });
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
  vi.useRealTimers();
  document.documentElement.removeAttribute('style');
  document.body.removeAttribute('style');
});

function renderFAB(props: Partial<React.ComponentProps<typeof FloatingActionButton>> = {}) {
  const defaults = {
    mode: 'songs' as const,
    onPress: vi.fn(),
  };
  return render(
    <TestProviders>
      <FloatingActionButton {...defaults} {...props} />
    </TestProviders>,
  );
}

function fireCancelableEvent(target: Element, type: string) {
  const event = new Event(type, { bubbles: true, cancelable: true });
  const preventDefaultSpy = vi.spyOn(event, 'preventDefault');
  const stopPropagationSpy = vi.spyOn(event, 'stopPropagation');
  fireEvent(target, event);
  return { preventDefaultSpy, stopPropagationSpy };
}

describe('FloatingActionButton', () => {
  it('renders the FAB button', () => {
    renderFAB();
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('uses glass styling for inactive icon-only main FABs when requested', () => {
    renderFAB({
      mode: 'players',
      directAction: true,
      surface: 'glass',
      ariaLabel: 'Filter Suggestions',
      icon: <span>F</span>,
    });

    const fab = screen.getByRole('button', { name: 'Filter Suggestions' });
    expect(fab.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(fab).not.toHaveStyle({ backgroundColor: '#2D82E6' });
  });

  it('positions the FAB above the bottom safe area', () => {
    renderFAB();
    const fabContainer = screen.getByRole('button', { name: /actions/i }).parentElement!;
    expect(fabContainer.style.bottom).toContain('var(--sab');
  });

  it('opens the popup menu when clicked', () => {
    const actionGroups: ActionItem[][] = [[
      { label: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
    ]];
    renderFAB({ actionGroups });

    const fab = screen.getByRole('button', { name: /actions/i });
    fireEvent.click(fab);

    expect(screen.getByText('Sort')).toBeTruthy();
  });

  it('closes the popup menu when clicked again', () => {
    const actionGroups: ActionItem[][] = [[
      { label: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
    ]];
    renderFAB({ actionGroups });

    const fab = screen.getByRole('button', { name: /actions/i });
    fireEvent.click(fab);
    expect(screen.getByText('Sort')).toBeTruthy();

    fireEvent.click(fab);
    // Menu is closing (popupVisible=false), but still mounted until animation ends
  });

  it('renders action groups with labels and icons', () => {
    const onSort = vi.fn();
    const onFilter = vi.fn();
    const actionGroups: ActionItem[][] = [
      [{ label: 'Sort', icon: <span data-testid="sort-icon">S</span>, onPress: onSort }],
      [{ label: 'Filter', icon: <span data-testid="filter-icon">F</span>, onPress: onFilter }],
    ];
    renderFAB({ actionGroups });

    fireEvent.click(screen.getByRole('button', { name: /actions/i }));

    expect(screen.getByText('Sort')).toBeTruthy();
    expect(screen.getByText('Filter')).toBeTruthy();
    expect(screen.getByTestId('sort-icon')).toBeTruthy();
    expect(screen.getByTestId('filter-icon')).toBeTruthy();
  });

  it('calls action.onPress when an action is clicked', () => {
    const onSort = vi.fn();
    const actionGroups: ActionItem[][] = [[
      { label: 'Sort', icon: <span>S</span>, onPress: onSort },
    ]];
    renderFAB({ actionGroups });

    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    fireEvent.click(screen.getByText('Sort'));
    expect(onSort).toHaveBeenCalled();
  });

  it('shows search bar when defaultOpen is true', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    expect(screen.getByPlaceholderText('Search songs...')).toBeTruthy();
  });

  it('renders a collapsed songs dock with evenly distributed direct actions', () => {
    const onSort = vi.fn();
    const onFilter = vi.fn();
    const onQuickLinks = vi.fn();

    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [
        { label: 'Sort Songs', icon: <span>S</span>, onPress: onSort },
        { label: 'Filter Songs', icon: <span>F</span>, onPress: onFilter },
      ],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: onQuickLinks,
    });

    const dock = document.querySelector('.fab-search-dock') as HTMLElement;
    expect(dock).toBeTruthy();
    expect(screen.getByTestId('fab-dock-row')).toHaveStyle({ justifyContent: 'space-between' });
    expect(screen.queryByRole('textbox')).toBeNull();
    expect(screen.getByRole('button', { name: 'Search' })).toBeTruthy();
    const sortButton = screen.getByRole('button', { name: 'Sort Songs' });
    const filterButton = screen.getByRole('button', { name: 'Filter Songs' });
    expect(sortButton).toBeTruthy();
    expect(filterButton).toBeTruthy();
    expect(sortButton.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(filterButton.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    fireEvent.click(screen.getByRole('button', { name: 'Sort Songs' }));
    fireEvent.click(screen.getByRole('button', { name: 'Filter Songs' }));
    fireEvent.click(screen.getByRole('button', { name: 'Quick Links' }));
    expect(onSort).toHaveBeenCalledTimes(1);
    expect(onFilter).toHaveBeenCalledTimes(1);
    expect(onQuickLinks).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('fab-menu')).toBeNull();
  });

  it('renders non-search side actions beside the FAB without duplicating menu items', () => {
    const onPaths = vi.fn();
    const onQuickLinks = vi.fn();

    renderFAB({
      mode: 'players',
      sideActions: [{ label: 'View Paths', icon: <span>P</span>, onPress: onPaths }],
      actionGroups: [[{ label: 'Quick Links', icon: <span>Q</span>, onPress: onQuickLinks }]],
    });

    const sideActions = screen.getByTestId('fab-side-actions');
    const pathsButton = within(sideActions).getByRole('button', { name: 'View Paths' });
    expect(within(pathsButton).getByText('View Paths')).toBeTruthy();
    expect(pathsButton).toHaveStyle({ minWidth: `${Layout.fabSize}px`, height: `${Layout.fabSize}px` });
    expect(pathsButton.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(pathsButton.style.opacity).toBe('1');

    fireEvent.click(pathsButton);
    expect(onPaths).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByRole('button', { name: 'Actions' }));
    const menu = screen.getByTestId('fab-menu');
    expect(within(menu).getByText('Quick Links')).toBeTruthy();
    expect(within(menu).queryByText('View Paths')).toBeNull();
  });

  it('renders external side actions without an inert main FAB', () => {
    const onShop = vi.fn();

    renderFAB({
      mode: 'players',
      sideActions: [{
        label: 'Item Shop',
        icon: <span>S</span>,
        href: 'https://example.com/shop/s1',
        target: '_blank',
        rel: 'noopener noreferrer',
        tone: 'accent',
        onPress: onShop,
      }],
    });

    const link = screen.getByRole('link', { name: 'Item Shop' });
    expect(link).toHaveAttribute('href', 'https://example.com/shop/s1');
    expect(link).toHaveAttribute('target', '_blank');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(within(link).getByText('Item Shop')).toBeTruthy();
    expect(link).toHaveStyle({ minWidth: `${Layout.fabSize}px`, height: `${Layout.fabSize}px` });
    expect(link.style.backgroundColor).toBe('rgb(45, 130, 230)');
    expect(screen.queryByRole('button', { name: 'Actions' })).toBeNull();
  });

  it('uses opaque default glass for icon-only side actions while active side actions stay semantic', () => {
    renderFAB({
      mode: 'players',
      sideActions: [
        { label: 'Change Ranking', iconOnly: true, icon: <span>R</span>, onPress: vi.fn() },
        { label: 'Filter', iconOnly: true, active: true, icon: <span>F</span>, onPress: vi.fn() },
      ],
    });

    const sideActions = screen.getByTestId('fab-side-actions');
    const rankingButton = within(sideActions).getByRole('button', { name: 'Change Ranking' });
    const filterButton = within(sideActions).getByRole('button', { name: 'Filter' });

    expect(rankingButton.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(rankingButton.style.opacity).toBe('1');
    expect(filterButton.style.backgroundColor).toBe('rgb(45, 130, 230)');
    expect(filterButton.style.backgroundImage).toBe('none');
  });

  it('renders icon-only side action accessories inside a compact active pill', () => {
    renderFAB({
      mode: 'players',
      sideActions: [
        { label: 'Change Ranking', iconOnly: true, icon: <span data-testid="ranking-icon">R</span>, onPress: vi.fn() },
        {
          label: 'Lead / Bass',
          iconOnly: true,
          active: true,
          icon: <span data-testid="filter-icon">F</span>,
          iconAccessory: (
            <span data-testid="combo-icons">
              <img alt="Solo_Guitar" />
              <img alt="Solo_Bass" />
            </span>
          ),
          onPress: vi.fn(),
        },
      ],
    });

    const sideActions = screen.getByTestId('fab-side-actions');
    const rankingButton = within(sideActions).getByRole('button', { name: 'Change Ranking' });
    const filterButton = within(sideActions).getByRole('button', { name: 'Lead / Bass' });

    expect(within(rankingButton).getByTestId('ranking-icon')).toBeTruthy();
    expect(within(rankingButton).queryByTestId('combo-icons')).toBeNull();
    expect(within(filterButton).getByTestId('filter-icon')).toBeTruthy();
    expect(within(filterButton).getByTestId('combo-icons')).toBeTruthy();
    expect(within(filterButton).queryByText('Lead / Bass')).toBeNull();
    expect(filterButton).toHaveStyle({ minWidth: `${Layout.fabSize}px`, height: `${Layout.fabSize}px` });
    expect(filterButton.style.backgroundColor).toBe('rgb(45, 130, 230)');
    expect(Array.from(filterButton.children).map(child => child.getAttribute('data-testid'))).toEqual(['filter-icon', 'combo-icons']);
  });

  it('shows the Search label while dock actions stay circular when measured width fits', () => {
    mockDockLabelMeasurements(420, [104, 84, 92]);

    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [
        { label: 'Sort Songs', displayLabel: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
        { label: 'Filter Songs', displayLabel: 'Filter', icon: <span>F</span>, onPress: vi.fn() },
      ],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: vi.fn(),
    });

    const searchButton = screen.getByRole('button', { name: 'Search' });
    const sortButton = screen.getByRole('button', { name: 'Sort Songs' });
    const filterButton = screen.getByRole('button', { name: 'Filter Songs' });
    const quickLinksButton = screen.getByRole('button', { name: 'Quick Links' });

    expect(within(searchButton).getByText('Search')).toBeTruthy();
    expect(within(sortButton).queryByText('Sort')).toBeNull();
    expect(within(filterButton).queryByText('Filter')).toBeNull();
    expect(within(quickLinksButton).queryByText(/quick links/i)).toBeNull();
    expect(screen.getByTestId('fab-dock-row')).toHaveStyle({ justifyContent: 'flex-start', gap: '4px' });
    expect(searchButton.parentElement).toHaveStyle({ width: '240px', minWidth: '104px' });
    expect(searchButton).toHaveStyle({ width: '100%', justifyContent: 'flex-start' });
    expect(sortButton).toHaveStyle({ width: '56px', height: '56px' });
    expect(sortButton.parentElement).toHaveStyle({ width: '56px' });
    expect(filterButton).toHaveStyle({ width: '56px', height: '56px' });
    expect(filterButton.parentElement).toHaveStyle({ width: '56px' });
    expect(sortButton.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(filterButton.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
  });

  it('keeps the initial songs dock hidden until measured widths settle', () => {
    mockDockLabelMeasurements(420, [104, 84, 92]);
    const rafCallbacks = new Map<number, FrameRequestCallback>();
    let nextRafId = 0;
    vi.mocked(window.requestAnimationFrame).mockImplementation((callback) => {
      const frameId = ++nextRafId;
      rafCallbacks.set(frameId, callback);
      return frameId;
    });
    vi.mocked(window.cancelAnimationFrame).mockImplementation((frameId) => {
      rafCallbacks.delete(frameId);
    });

    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [
        { label: 'Sort Songs', displayLabel: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
        { label: 'Filter Songs', displayLabel: 'Filter', icon: <span>F</span>, onPress: vi.fn() },
      ],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: vi.fn(),
    });

    const visibleContent = screen.getByTestId('fab-dock-visible-content');
    const searchButton = screen.getByRole('button', { name: 'Search' });

    expect(visibleContent).toHaveStyle({ opacity: '0' });
    expect(searchButton.parentElement).toHaveStyle({ width: '240px', minWidth: '104px' });
    expect(searchButton.parentElement).toHaveStyle({ transition: 'none' });

    const runNextAnimationFrame = (time: number) => {
      const nextFrame = rafCallbacks.entries().next().value;
      if (!nextFrame) throw new Error('Expected a queued animation frame');
      const [frameId, callback] = nextFrame;
      rafCallbacks.delete(frameId);
      callback(time);
    };

    act(() => { runNextAnimationFrame(0); });
    expect(visibleContent).toHaveStyle({ opacity: '0' });
    act(() => { runNextAnimationFrame(16); });
    expect(visibleContent).toHaveStyle({ opacity: '1' });
    expect((searchButton.parentElement as HTMLElement).style.transition).toContain('width 360ms ease');
  });

  it('uses blue active styling for active dock actions', () => {
    mockDockLabelMeasurements(420, [104, 84, 92]);

    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [
        { label: 'Sort Songs', displayLabel: 'Sort', active: true, icon: <span>S</span>, onPress: vi.fn() },
        { label: 'Filter Songs', displayLabel: 'Filter', icon: <span>F</span>, onPress: vi.fn() },
      ],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: vi.fn(),
    });

    const sortButton = screen.getByRole('button', { name: 'Sort Songs' });
    const filterButton = screen.getByRole('button', { name: 'Filter Songs' });

    expect(sortButton.style.backgroundColor).toBe('rgb(45, 130, 230)');
    expect(sortButton.style.backgroundImage).toBe('none');
    expect(sortButton).toHaveStyle({ width: '56px', height: '56px' });
    expect(within(sortButton).queryByText('Sort')).toBeNull();
    expect(filterButton.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
  });

  it('keeps dock controls icon-only when measured labels do not fit', () => {
    mockDockLabelMeasurements(280, [104, 84, 92]);

    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [
        { label: 'Sort Songs', displayLabel: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
        { label: 'Filter Songs', displayLabel: 'Filter', icon: <span>F</span>, onPress: vi.fn() },
      ],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: vi.fn(),
    });

    const searchButton = screen.getByRole('button', { name: 'Search' });
    const sortButton = screen.getByRole('button', { name: 'Sort Songs' });
    const filterButton = screen.getByRole('button', { name: 'Filter Songs' });

    expect(within(searchButton).queryByText('Search')).toBeNull();
    expect(within(sortButton).queryByText('Sort')).toBeNull();
    expect(within(filterButton).queryByText('Filter')).toBeNull();
    expect(searchButton.parentElement).toHaveStyle({ width: '56px' });
    expect(sortButton.parentElement).toHaveStyle({ width: '56px' });
    expect(filterButton.parentElement).toHaveStyle({ width: '56px' });
  });

  it('can show the Search label when Filter is unavailable while Sort stays circular', () => {
    mockDockLabelMeasurements(300, [104, 84]);

    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [
        { label: 'Sort Songs', displayLabel: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
      ],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: vi.fn(),
    });

    const searchButton = screen.getByRole('button', { name: 'Search' });
    const sortButton = screen.getByRole('button', { name: 'Sort Songs' });

    expect(within(searchButton).getByText('Search')).toBeTruthy();
    expect(within(sortButton).queryByText('Sort')).toBeNull();
    expect(searchButton.parentElement).toHaveStyle({ width: '180px', minWidth: '104px' });
    expect(sortButton).toHaveStyle({ width: '56px', height: '56px' });
    expect(screen.queryByRole('button', { name: 'Filter Songs' })).toBeNull();
  });

  it('contracts labeled dock search directly to its stretched collapsed width', () => {
    mockDockLabelMeasurements(420, [104, 84, 92]);

    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [
        { label: 'Sort Songs', displayLabel: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
        { label: 'Filter Songs', displayLabel: 'Filter', icon: <span>F</span>, onPress: vi.fn() },
      ],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: vi.fn(),
    });

    const collapsedSearchButton = screen.getByRole('button', { name: 'Search' });
    expect(collapsedSearchButton).toHaveStyle({ padding: '0px 12px', gap: '8px' });

    fireEvent.click(collapsedSearchButton);
    act(() => { vi.advanceTimersByTime(700); });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const searchInputWrap = input.parentElement as HTMLElement;
    const searchSlot = input.closest('.fab-search-bar')?.parentElement as HTMLElement;

    expect(searchInputWrap).toHaveStyle({ padding: '0px 12px', gap: '8px' });

    fireEvent.focus(input);
    fireEvent.blur(input);
    act(() => { vi.advanceTimersByTime(300); });

    expect(searchSlot).toHaveStyle({ width: '240px', flexBasis: '240px' });
  });

  it('expands dock search, compacts on Enter blur, and preserves the query', () => {
    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [{ label: 'Sort Songs', icon: <span>S</span>, onPress: vi.fn() }],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: vi.fn(),
    });

    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    const hiddenInput = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const expandedSearchIcon = hiddenInput.parentElement?.querySelector('svg');
    expect(document.activeElement).toBe(hiddenInput);
    expect(hiddenInput).toHaveStyle({ opacity: '0' });
    expect(expandedSearchIcon?.getAttribute('width')).toBe('18');
    expect(expandedSearchIcon?.getAttribute('height')).toBe('18');
    const sortSlot = screen.getByRole('button', { name: 'Sort Songs' }).parentElement as HTMLElement;
    expect(sortSlot).toHaveStyle({ opacity: '0' });
    expect(sortSlot.style.width).not.toBe('0px');

    act(() => { vi.advanceTimersByTime(300); });
    expect(sortSlot.style.width).toBe('0px');
    expect(hiddenInput).toHaveStyle({ opacity: '0' });
    act(() => { vi.advanceTimersByTime(400); });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    expect(input).toHaveStyle({ opacity: '1' });
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'alpha' } });

    expect(input.value).toBe('alpha');

    fireEvent.keyDown(input, { key: 'Enter' });
    fireEvent.blur(input);
    expect(screen.getByPlaceholderText('Search songs...')).toBeTruthy();
    expect(input).toHaveStyle({ opacity: '1' });
    act(() => { vi.advanceTimersByTime(300); });
    expect(screen.queryByPlaceholderText('Search songs...')).toBeTruthy();
    expect(sortSlot.style.width).not.toBe('0px');
    expect(sortSlot).toHaveStyle({ opacity: '0' });
    act(() => { vi.advanceTimersByTime(400); });
    expect(screen.queryByPlaceholderText('Search songs...')).toBeNull();
    act(() => { vi.advanceTimersByTime(40); });
    const compactedSearchButton = screen.getByRole('button', { name: 'Search' });
    const compactedQueryText = within(compactedSearchButton).getByText('alpha');
    expect(screen.getByTestId('fab-search-toggle-icon')).toHaveStyle({ opacity: '1' });
    expect(compactedSearchButton.childElementCount).toBe(1);
    expect(within(compactedSearchButton).queryByText('Search')).toBeNull();
    expect(compactedQueryText).toHaveStyle({ fontSize: `${Font.md}px`, fontWeight: '400', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' });
    expect(sortSlot).toHaveStyle({ opacity: '1' });

    fireEvent.click(compactedSearchButton);
    const reopenedInput = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    expect(reopenedInput.value).toBe('alpha');
    expect(reopenedInput).toHaveStyle({ opacity: '1' });
    expect(document.activeElement).toBe(reopenedInput);
    act(() => { vi.advanceTimersByTime(700); });
  });

  it('clears dock search without compacting while focused', () => {
    renderFAB({
      defaultOpen: true,
      placeholder: 'Search songs...',
      dockActions: [{ label: 'Sort Songs', icon: <span>S</span>, onPress: vi.fn() }],
      directAction: true,
      ariaLabel: 'Quick Links',
      onPress: vi.fn(),
    });

    fireEvent.click(screen.getByRole('button', { name: 'Search' }));
    act(() => { vi.advanceTimersByTime(700); });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    fireEvent.focus(input);
    fireEvent.change(input, { target: { value: 'alpha' } });

    const clearButton = screen.getByRole('button', { name: 'Clear Search' });
    const clearIcon = clearButton.querySelector('svg') as SVGElement;
    const pointerDownSpies = fireCancelableEvent(clearIcon, 'pointerdown');
    expect(input.value).toBe('');

    expect(pointerDownSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(pointerDownSpies.stopPropagationSpy).toHaveBeenCalled();
    expect(screen.getByPlaceholderText('Search songs...')).toBeTruthy();
    expect(input.value).toBe('');
    expect(document.activeElement).toBe(input);

    fireEvent.change(input, { target: { value: 'beta' } });
    const clickClearButton = screen.getByRole('button', { name: 'Clear Search' });
    fireEvent.click(clickClearButton);
    expect(input.value).toBe('');
    expect(document.activeElement).toBe(input);
  });

  it('search bar syncs with SearchQueryContext', () => {
    renderFAB({ defaultOpen: true });
    const input = screen.getByRole('textbox');
    fireEvent.change(input, { target: { value: 'test query' } });
    expect((input as HTMLInputElement).value).toBe('test query');
  });

  it('hides search bar when defaultOpen is false', () => {
    renderFAB({ defaultOpen: false });
    expect(screen.queryByRole('textbox')).toBeNull();
  });

  it('renders custom icon when provided', () => {
    renderFAB({ icon: <span data-testid="custom-icon">X</span> });
    expect(screen.getByTestId('custom-icon')).toBeTruthy();
  });

  it('calls onPress directly when directAction is true', () => {
    const onPress = vi.fn();
    renderFAB({
      ariaLabel: 'Filter Suggestions',
      directAction: true,
      icon: <span data-testid="filter-icon">F</span>,
      onPress,
    });

    fireEvent.click(screen.getByRole('button', { name: 'Filter Suggestions' }));

    expect(onPress).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(screen.getByTestId('filter-icon')).toBeTruthy();
  });

  it('renders a labeled direct-action FAB without opening a menu', () => {
    const onPress = vi.fn();
    renderFAB({
      ariaLabel: 'Switch to list view',
      label: 'List',
      directAction: true,
      icon: <span data-testid="list-icon">L</span>,
      actionGroups: [[{ label: 'Sort', icon: <span>S</span>, onPress: vi.fn() }]],
      onPress,
    });

    const button = screen.getByRole('button', { name: 'Switch to list view' });
    expect(within(button).getByText('List')).toBeTruthy();
    expect(button.getAttribute('title')).toBe('Switch to list view');
    expect(button).toHaveStyle({ justifyContent: 'flex-start', gap: '8px', minWidth: '56px', padding: '0px 18px' });

    fireEvent.click(button);

    expect(onPress).toHaveBeenCalledTimes(1);
    expect(screen.queryByTestId('fab-menu')).toBeNull();
    expect(screen.queryByText('Sort')).toBeNull();
    expect(screen.getByTestId('list-icon')).toBeTruthy();
  });

  it('closes popup on outside click and prevents default', () => {
    const actionGroups: ActionItem[][] = [[
      { label: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
    ]];
    renderFAB({ actionGroups });

    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    expect(screen.getByText('Sort')).toBeTruthy();

    // Dispatch a click on document body; capture-phase handler should preventDefault + stopPropagation
    const event = new MouseEvent('click', { bubbles: true, cancelable: true });
    const pdSpy = vi.spyOn(event, 'preventDefault');
    const spSpy = vi.spyOn(event, 'stopPropagation');
    document.body.dispatchEvent(event);

    expect(pdSpy).toHaveBeenCalled();
    expect(spSpy).toHaveBeenCalled();
  });

  it('renders with default placeholder when none provided', () => {
    renderFAB({ defaultOpen: true });
    // Should render a search input with the default placeholder from i18n
    expect(screen.getByRole('textbox')).toBeTruthy();
  });

  it('blurs input on Enter key', () => {
    renderFAB({ defaultOpen: true });
    const input = screen.getByRole('textbox');
    const blurSpy = vi.spyOn(input, 'blur');
    expect(input.getAttribute('enterkeyhint')).toBe('search');
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(blurSpy).toHaveBeenCalled();
  });

  it('moves only the floating search and FAB controls when the iOS keyboard opens', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...');
    const searchOuter = input.closest('.fab-search-bar')?.parentElement as HTMLElement;
    const fabContainer = screen.getByRole('button', { name: /actions/i }).parentElement as HTMLElement;

    fireEvent.pointerDown(searchOuter);
    fireEvent.focus(input);

    act(() => {
      visualViewport.set(544, 0);
      vi.runOnlyPendingTimers();
    });

    expect(document.body.style.position).toBe('');
    expect(searchOuter.style.transform).toBe('translate3d(0, -300px, 0)');
    expect(fabContainer.style.transform).toBe('translate3d(0, -300px, 0)');
    expect(document.documentElement.style.getPropertyValue(SONGS_FAB_KEYBOARD_INSET_VAR)).toBe('300px');
    expect(document.documentElement.style.getPropertyValue(SONGS_FAB_KEYBOARD_OCCLUDED_BOTTOM_VAR)).toContain('300px');
  });

  it('keeps the search bar opaque while the keyboard opens and dismisses', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...');
    const inputWrap = input.parentElement as HTMLElement;
    const searchOuter = input.closest('.fab-search-bar')?.parentElement as HTMLElement;

    expect(inputWrap.style.opacity).toBe('1');
    expect(inputWrap.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(inputWrap.style.transition).not.toContain('opacity');

    fireEvent.pointerDown(searchOuter);
    fireEvent.focus(input);

    act(() => {
      visualViewport.set(544, 0);
      vi.runOnlyPendingTimers();
    });

    expect(inputWrap.style.opacity).toBe('1');
    expect(inputWrap.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');
    expect(inputWrap.style.transition).not.toContain('opacity');

    fireEvent.blur(input);

    act(() => {
      vi.runOnlyPendingTimers();
    });

    expect(inputWrap.style.opacity).toBe('1');
    expect(document.documentElement.style.getPropertyValue(SONGS_FAB_KEYBOARD_INSET_VAR)).toBe('0px');
  });

  it('focuses the search input with preventScroll from the wrapper click gesture', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const searchOuter = input.closest('.fab-search-bar')?.parentElement as HTMLElement;
    const focusSpy = vi.spyOn(input, 'focus');

    const pointerEventSpies = fireCancelableEvent(searchOuter, 'pointerdown');

    expect(focusSpy).not.toHaveBeenCalled();
    expect(pointerEventSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(pointerEventSpies.stopPropagationSpy).toHaveBeenCalled();

    const clickEventSpies = fireCancelableEvent(searchOuter, 'click');

    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true });
    expect(clickEventSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(clickEventSpies.stopPropagationSpy).toHaveBeenCalled();
  });

  it('focuses direct search input taps through the controlled no-scroll gesture path', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const focusSpy = vi.spyOn(input, 'focus');

    const pointerDownSpies = fireCancelableEvent(input, 'pointerdown');

    expect(pointerDownSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(pointerDownSpies.stopPropagationSpy).toHaveBeenCalled();
    expect(focusSpy).not.toHaveBeenCalled();

    const pointerUpSpies = fireCancelableEvent(input, 'pointerup');
    const clickSpies = fireCancelableEvent(input, 'click');

    expect(pointerUpSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(pointerUpSpies.stopPropagationSpy).toHaveBeenCalled();
    expect(clickSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(clickSpies.stopPropagationSpy).toHaveBeenCalled();
    expect(focusSpy).toHaveBeenCalledTimes(1);
    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true });
  });

  it('deduplicates touch and click completions for one search focus gesture', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const focusSpy = vi.spyOn(input, 'focus').mockImplementation(() => undefined);

    fireCancelableEvent(input, 'pointerdown');
    fireCancelableEvent(input, 'touchend');
    fireCancelableEvent(input, 'click');

    expect(focusSpy).toHaveBeenCalledTimes(1);
    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true });
  });

  it('does not rely on browser auto-scroll restoration for direct input focus', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const focusSpy = vi.spyOn(input, 'focus');

    Object.defineProperty(window, 'scrollY', { configurable: true, value: 180 });
    document.documentElement.scrollTop = 180;
    document.body.scrollTop = 90;

    fireCancelableEvent(input, 'pointerdown');
    fireCancelableEvent(input, 'click');

    expect(window.scrollTo).not.toHaveBeenCalled();
    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true });
    expect(document.documentElement.scrollTop).toBe(180);
    expect(document.body.scrollTop).toBe(90);
    expect(document.activeElement).toBe(input);
  });

  it('does not manually refocus an already-focused input on direct tap or click', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;

    act(() => {
      input.focus();
      fireEvent.focus(input);
    });
    const focusSpy = vi.spyOn(input, 'focus');

    const pointerEventSpies = fireCancelableEvent(input, 'pointerdown');
    const clickEventSpies = fireCancelableEvent(input, 'click');

    expect(pointerEventSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(pointerEventSpies.stopPropagationSpy).toHaveBeenCalled();
    expect(clickEventSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(clickEventSpies.stopPropagationSpy).toHaveBeenCalled();
    expect(focusSpy).not.toHaveBeenCalled();
  });

  it('protects an already-focused input from wrapper edge taps without refocusing', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const searchOuter = input.closest('.fab-search-bar')?.parentElement as HTMLElement;

    act(() => {
      input.focus();
      fireEvent.focus(input);
    });
    const focusSpy = vi.spyOn(input, 'focus');

    const pointerEventSpies = fireCancelableEvent(searchOuter, 'pointerdown');
    const clickEventSpies = fireCancelableEvent(searchOuter, 'click');

    expect(pointerEventSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(pointerEventSpies.stopPropagationSpy).toHaveBeenCalled();
    expect(clickEventSpies.preventDefaultSpy).toHaveBeenCalled();
    expect(clickEventSpies.stopPropagationSpy).toHaveBeenCalled();
    expect(document.activeElement).toBe(input);
    expect(focusSpy).not.toHaveBeenCalled();
  });

  it('preserves search input focus while the iOS keyboard viewport settles', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const blurSpy = vi.spyOn(input, 'blur');

    act(() => {
      input.focus();
      fireEvent.focus(input);
    });
    expect(document.activeElement).toBe(input);

    act(() => {
      visualViewport.set(544, 0);
      vi.runOnlyPendingTimers();
    });

    expect(document.activeElement).toBe(input);
    expect(blurSpy).not.toHaveBeenCalled();
    expect(document.documentElement.style.getPropertyValue(SONGS_FAB_KEYBOARD_INSET_VAR)).toBe('300px');
  });

  it('uses the pre-keyboard baseline when PWA innerHeight shrinks with the keyboard', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...');
    const searchOuter = input.closest('.fab-search-bar')?.parentElement as HTMLElement;

    fireEvent.pointerDown(searchOuter);
    fireEvent.focus(input);
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: 544 });

    act(() => {
      visualViewport.set(544, 0);
      vi.runOnlyPendingTimers();
    });

    expect(searchOuter.style.transform).toBe('translate3d(0, -300px, 0)');
  });

  it('renders without action groups', () => {
    renderFAB({ actionGroups: undefined });
    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    // Should not throw — FABMenu receives empty array
  });

  it('menu container has data-glow-scope attribute when open', () => {
    const actionGroups: ActionItem[][] = [[
      { label: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
    ]];
    renderFAB({ actionGroups });

    fireEvent.click(screen.getByRole('button', { name: /actions/i }));

    const sortButton = screen.getByText('Sort');
    const menu = sortButton.closest('[data-glow-scope]');
    expect(menu).toBeTruthy();
  });

  it('menu items have frosted-card marker for light painting', () => {
    const actionGroups: ActionItem[][] = [[
      { label: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
    ]];
    renderFAB({ actionGroups });

    fireEvent.click(screen.getByRole('button', { name: /actions/i }));

    const sortButton = screen.getByText('Sort').closest('button');
    expect(sortButton?.style.getPropertyValue('--frosted-card')).toBeTruthy();
  });
});
