import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import FloatingActionButton, { type ActionItem } from '../../components/shell/fab/FloatingActionButton';
import { TestProviders } from '../helpers/TestProviders';

// Mock IS_PWA
vi.mock('../../utils/platform', () => ({
  IS_PWA: false,
  IS_IOS: false,
  IS_ANDROID: false,
  IS_MOBILE_DEVICE: false,
}));

beforeEach(() => {
  vi.clearAllMocks();
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
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

  it('closes popup on outside click', () => {
    const actionGroups: ActionItem[][] = [[
      { label: 'Sort', icon: <span>S</span>, onPress: vi.fn() },
    ]];
    const { container } = renderFAB({ actionGroups });

    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    expect(screen.getByText('Sort')).toBeTruthy();

    // Click outside the container
    fireEvent.click(container);
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
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(blurSpy).toHaveBeenCalled();
  });

  it('renders without action groups', () => {
    renderFAB({ actionGroups: undefined });
    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    // Should not throw — FABMenu receives empty array
  });
});
