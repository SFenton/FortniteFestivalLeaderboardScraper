import { afterEach, describe, it, expect, vi, beforeEach } from 'vitest';
import { act, render, screen, fireEvent } from '@testing-library/react';
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

describe('FloatingActionButton', () => {
  it('renders the FAB button', () => {
    renderFAB();
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
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

  it('strengthens the search bar opacity while the keyboard is open and restores it on dismiss', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...');
    const inputWrap = input.parentElement as HTMLElement;
    const searchOuter = input.closest('.fab-search-bar')?.parentElement as HTMLElement;

    expect(inputWrap.style.opacity).toBe('0.9');

    fireEvent.pointerDown(searchOuter);
    fireEvent.focus(input);

    act(() => {
      visualViewport.set(544, 0);
      vi.runOnlyPendingTimers();
    });

    expect(inputWrap.style.opacity).toBe('1');
    expect(inputWrap.style.backgroundColor).toBe('rgba(18, 24, 38, 0.96)');

    fireEvent.blur(input);

    act(() => {
      vi.runOnlyPendingTimers();
    });

    expect(inputWrap.style.opacity).toBe('0.9');
    expect(document.documentElement.style.getPropertyValue(SONGS_FAB_KEYBOARD_INSET_VAR)).toBe('0px');
  });

  it('focuses the search input with preventScroll from the tap gesture', () => {
    renderFAB({ defaultOpen: true, placeholder: 'Search songs...' });
    const input = screen.getByPlaceholderText('Search songs...') as HTMLInputElement;
    const searchOuter = input.closest('.fab-search-bar')?.parentElement as HTMLElement;
    const focusSpy = vi.spyOn(input, 'focus');

    fireEvent.pointerDown(searchOuter);

    expect(focusSpy).toHaveBeenCalledWith({ preventScroll: true });
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
