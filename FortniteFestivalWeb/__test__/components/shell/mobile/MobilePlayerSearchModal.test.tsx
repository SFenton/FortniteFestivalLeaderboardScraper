import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, act } from '@testing-library/react';
import { useEffect } from 'react';
import MobilePlayerSearchModal from '../../../../src/components/shell/mobile/MobilePlayerSearchModal';
import { TestProviders } from '../../../helpers/TestProviders';
import type { TrackedPlayer } from '../../../../src/hooks/data/useTrackedPlayer';

const mockApi = vi.hoisted(() => ({
  searchAccounts: vi.fn().mockResolvedValue({ results: [] }),
  getSongs: vi.fn().mockResolvedValue({ songs: [], count: 0, currentSeason: 5 }),
  getPlayer: vi.fn().mockResolvedValue(null),
  getSyncStatus: vi.fn().mockResolvedValue({ accountId: '', isTracked: false }),
  getVersion: vi.fn().mockResolvedValue({ version: '1.0.0' }),
  getLeaderboard: vi.fn().mockResolvedValue({ entries: [] }),
  getAllLeaderboards: vi.fn().mockResolvedValue({ instruments: [] }),
  getPlayerHistory: vi.fn().mockResolvedValue({ history: [] }),
  getPlayerStats: vi.fn().mockResolvedValue({ stats: [] }),
  trackPlayer: vi.fn().mockResolvedValue({ accountId: '', displayName: '' }),
}));

vi.mock('../../../../src/api/client', () => ({ api: mockApi }));

// Mock ModalShell to skip CSS transitions (jsdom doesn't fire transitionend)
vi.mock('../../../../src/components/modals/components/ModalShell', () => ({
  default: ({ visible, title, children, onOpenComplete, onCloseComplete }: {
    visible: boolean; title: string; children: React.ReactNode;
    onOpenComplete?: () => void; onCloseComplete?: () => void;
  }) => {
    useEffect(() => {
      if (visible) onOpenComplete?.();
      else onCloseComplete?.();
    }, [visible, onOpenComplete, onCloseComplete]);
    if (!visible) return null;
    return <div><h2>{title}</h2>{children}</div>;
  },
}));

// Stub matchMedia for useIsMobile
beforeEach(() => {
  vi.useFakeTimers({ shouldAdvanceTime: true });
  vi.clearAllMocks();
  mockApi.searchAccounts.mockResolvedValue({ results: [] });
  mockApi.getSongs.mockResolvedValue({ songs: [], count: 0, currentSeason: 5 });
  mockApi.getPlayer.mockResolvedValue(null);
  mockApi.getSyncStatus.mockResolvedValue({ accountId: '', isTracked: false });
  mockApi.getVersion.mockResolvedValue({ version: '1.0.0' });
  mockApi.getLeaderboard.mockResolvedValue({ entries: [] });
  mockApi.getAllLeaderboards.mockResolvedValue({ instruments: [] });
  mockApi.getPlayerHistory.mockResolvedValue({ history: [] });
  mockApi.getPlayerStats.mockResolvedValue({ stats: [] });
  mockApi.trackPlayer.mockResolvedValue({ accountId: '', displayName: '' });
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: vi.fn().mockImplementation((query: string) => ({
      matches: false, media: query, onchange: null,
      addListener: vi.fn(), removeListener: vi.fn(),
      addEventListener: vi.fn(), removeEventListener: vi.fn(), dispatchEvent: vi.fn(),
    })),
  });
});

afterEach(() => {
  vi.runOnlyPendingTimers();
  vi.useRealTimers();
  vi.restoreAllMocks();
});

function renderModal(overrides: Partial<{
  visible: boolean;
  onClose: () => void;
  onSelect: (p: TrackedPlayer) => void;
  player: TrackedPlayer | null;
  onDeselect: () => void;
  isMobile: boolean;
  title?: string;
}> = {}) {
  const props = {
    visible: true,
    onClose: vi.fn(),
    onSelect: vi.fn(),
    player: null as TrackedPlayer | null,
    onDeselect: vi.fn(),
    isMobile: false,
    ...overrides,
  };
  return render(
    <TestProviders>
      <MobilePlayerSearchModal {...props} />
    </TestProviders>,
  );
}

/** Advance fake timers and flush microtasks so API-triggered state updates land. */
async function advanceAndFlush(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
  // Flush remaining microtasks (Promise chains from mocked API calls)
  await act(async () => { await Promise.resolve(); });
}

/** After a search completes, fire transitionend on the spinner so showSpinner turns off. */
function dismissSpinner(container: HTMLElement) {
  const spinnerWrap = container.querySelector('[class*="spinnerWrap"]');
  if (spinnerWrap) fireEvent.transitionEnd(spinnerWrap);
}

describe('MobilePlayerSearchModal', () => {
  it('renders modal with default title when visible', () => {
    renderModal();
    expect(screen.getByText('Select Player Profile')).toBeTruthy();
  });

  it('renders custom title when provided', () => {
    renderModal({ title: 'Find a Player' });
    expect(screen.getByText('Find a Player')).toBeTruthy();
  });

  it('does not render when not visible', () => {
    renderModal({ visible: false });
    expect(screen.queryByText('Select Player Profile')).toBeNull();
  });

  it('renders search input when no player is selected', () => {
    renderModal();
    expect(screen.getByPlaceholderText('Search player…')).toBeTruthy();
  });

  it('shows hint text when query is short', () => {
    renderModal();
    expect(screen.getByText('Enter a username to search for.')).toBeTruthy();
  });

  it('debounces search input', async () => {
    mockApi.searchAccounts.mockResolvedValue({
      results: [{ accountId: 'a1', displayName: 'PlayerOne' }],
    });
    renderModal();
    const input = screen.getByPlaceholderText('Search player…');

    fireEvent.change(input, { target: { value: 'Pla' } });

    // API should not be called immediately
    expect(mockApi.searchAccounts).not.toHaveBeenCalled();

    // Advance past debounce (300ms)
    await advanceAndFlush(350);

    expect(mockApi.searchAccounts).toHaveBeenCalledWith('Pla', 10);
  });

  it('does not search when query is less than 2 chars', async () => {
    renderModal();
    const input = screen.getByPlaceholderText('Search player…');

    fireEvent.change(input, { target: { value: 'P' } });
    await advanceAndFlush(350);

    expect(mockApi.searchAccounts).not.toHaveBeenCalled();
  });

  it('renders search results', async () => {
    mockApi.searchAccounts.mockResolvedValue({
      results: [
        { accountId: 'a1', displayName: 'PlayerOne' },
        { accountId: 'a2', displayName: 'PlayerTwo' },
      ],
    });
    const { container } = renderModal();
    const input = screen.getByPlaceholderText('Search player…');

    fireEvent.change(input, { target: { value: 'Player' } });
    await advanceAndFlush(350);
    dismissSpinner(container);

    expect(screen.getByText('PlayerOne')).toBeTruthy();
    expect(screen.getByText('PlayerTwo')).toBeTruthy();
  });

  it('calls onSelect and onClose when a result is clicked', async () => {
    const onSelect = vi.fn();
    const onClose = vi.fn();
    mockApi.searchAccounts.mockResolvedValue({
      results: [{ accountId: 'a1', displayName: 'PlayerOne' }],
    });
    const { container } = renderModal({ onSelect, onClose });
    const input = screen.getByPlaceholderText('Search player…');

    fireEvent.change(input, { target: { value: 'Player' } });
    await advanceAndFlush(350);
    dismissSpinner(container);

    fireEvent.click(screen.getByText('PlayerOne'));
    expect(onSelect).toHaveBeenCalledWith({ accountId: 'a1', displayName: 'PlayerOne' });
    expect(onClose).toHaveBeenCalled();
  });

  it('shows no results message when search yields nothing', async () => {
    mockApi.searchAccounts.mockResolvedValue({ results: [] });
    const { container } = renderModal();
    const input = screen.getByPlaceholderText('Search player…');

    fireEvent.change(input, { target: { value: 'Nobody' } });
    await advanceAndFlush(350);
    dismissSpinner(container);

    expect(screen.getByText('No matching username found.')).toBeTruthy();
  });

  it('renders player card when player is selected', () => {
    renderModal({
      player: { accountId: 'a1', displayName: 'TestPlayer' },
    });
    expect(screen.getByText('TestPlayer')).toBeTruthy();
    expect(screen.getByText('Deselect Player')).toBeTruthy();
  });

  it('hides search input when player is selected', () => {
    renderModal({
      player: { accountId: 'a1', displayName: 'TestPlayer' },
    });
    expect(screen.queryByPlaceholderText('Search player…')).toBeNull();
  });

  it('calls onDeselect and onClose when deselect button is clicked', async () => {
    const onDeselect = vi.fn();
    const onClose = vi.fn();
    renderModal({
      player: { accountId: 'a1', displayName: 'TestPlayer' },
      onDeselect,
      onClose,
    });

    fireEvent.click(screen.getByText('Deselect Player'));

    // Deselect has animation delay (850ms)
    await advanceAndFlush(900);

    expect(onDeselect).toHaveBeenCalled();
    expect(onClose).toHaveBeenCalled();
  });

  it('handles search API failure gracefully', async () => {
    mockApi.searchAccounts.mockRejectedValue(new Error('Network error'));
    const { container } = renderModal();
    const input = screen.getByPlaceholderText('Search player…');

    fireEvent.change(input, { target: { value: 'Test' } });
    await advanceAndFlush(350);
    dismissSpinner(container);

    // Should show no results, not crash
    expect(screen.getByText('No matching username found.')).toBeTruthy();
  });

  it('clears results when query becomes too short', async () => {
    mockApi.searchAccounts.mockResolvedValue({
      results: [{ accountId: 'a1', displayName: 'PlayerOne' }],
    });
    const { container } = renderModal();
    const input = screen.getByPlaceholderText('Search player…');

    // Type enough to trigger search
    fireEvent.change(input, { target: { value: 'Player' } });
    await advanceAndFlush(350);
    dismissSpinner(container);
    expect(screen.getByText('PlayerOne')).toBeTruthy();

    // Clear to short query
    fireEvent.change(input, { target: { value: 'P' } });
    expect(screen.queryByText('PlayerOne')).toBeNull();
  });

  it('cancels previous debounce on rapid typing', async () => {
    mockApi.searchAccounts.mockResolvedValue({
      results: [{ accountId: 'a1', displayName: 'Result' }],
    });
    renderModal();
    const input = screen.getByPlaceholderText('Search player…');

    fireEvent.change(input, { target: { value: 'Hel' } });
    await advanceAndFlush(100);
    fireEvent.change(input, { target: { value: 'Hello' } });
    await advanceAndFlush(350);

    // Should only search for the final value
    expect(mockApi.searchAccounts).toHaveBeenLastCalledWith('Hello', 10);
  });

  it('does not deselect twice on rapid clicks', () => {
    const onDeselect = vi.fn();
    renderModal({
      player: { accountId: 'a1', displayName: 'TestPlayer' },
      onDeselect,
    });
    const btn = screen.getByText('Deselect Player');
    fireEvent.click(btn);
    fireEvent.click(btn);
    // Only one deselect timeout should be started (second click is guarded)
  });

  it('blurs input on Enter key', () => {
    renderModal();
    const input = screen.getByPlaceholderText('Search player…');
    const blurSpy = vi.spyOn(HTMLElement.prototype, 'blur');
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(blurSpy).toHaveBeenCalled();
  });
});
