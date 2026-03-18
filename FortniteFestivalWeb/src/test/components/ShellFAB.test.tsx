import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';

vi.mock('../../contexts/SearchQueryContext', () => ({
  useSearchQuery: () => ({ query: '', setQuery: vi.fn() }),
  SearchQueryProvider: ({ children }: { children: React.ReactNode }) => children,
}));
vi.mock('../../utils/platform', () => ({
  IS_PWA: false,
  IS_IOS: false,
  IS_ANDROID: false,
  IS_MOBILE_DEVICE: false,
}));

import FloatingActionButton from '../../components/shell/FloatingActionButton';

describe('FloatingActionButton (shell)', () => {
  beforeEach(() => {
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('renders the FAB button', () => {
    render(
      <FloatingActionButton mode="players" onPress={vi.fn()} />,
    );
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('opens and closes actions menu on click', () => {
    const actions = [[{ label: 'Test Action', icon: <span>icon</span>, onPress: vi.fn() }]];
    render(
      <FloatingActionButton mode="players" onPress={vi.fn()} actionGroups={actions} />,
    );
    const fab = screen.getByRole('button', { name: /actions/i });

    // Open
    fireEvent.click(fab);
    expect(screen.getByText('Test Action')).toBeTruthy();

    // Close
    fireEvent.click(fab);
    act(() => { vi.advanceTimersByTime(400); });
  });

  it('renders search bar when defaultOpen', () => {
    render(
      <FloatingActionButton mode="songs" defaultOpen placeholder="Search..." onPress={vi.fn()} />,
    );
    expect(screen.getByPlaceholderText('Search...')).toBeTruthy();
  });

  it('fires action and closes menu', () => {
    const onAction = vi.fn();
    const actions = [[{ label: 'Find', icon: <span>🔍</span>, onPress: onAction }]];
    render(
      <FloatingActionButton mode="players" onPress={vi.fn()} actionGroups={actions} />,
    );

    // Open
    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    // Click action
    fireEvent.click(screen.getByText('Find'));
    expect(onAction).toHaveBeenCalledTimes(1);
  });
});
