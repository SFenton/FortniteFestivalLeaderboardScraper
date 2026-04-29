import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => ({ rivals: true, compete: true, leaderboards: true, firstRun: true }),
}));

const mockScrollRef = { current: null as HTMLDivElement | null };
vi.mock('../../../../src/contexts/ScrollContainerContext', () => ({
  useScrollContainer: () => mockScrollRef,
}));

import PinnedSidebar from '../../../../src/components/shell/desktop/PinnedSidebar';
import { SettingsProvider } from '../../../../src/contexts/SettingsContext';

function renderPinned(overrides: Partial<Parameters<typeof PinnedSidebar>[0]> = {}) {
  const defaults = {
    player: null,
    onDeselect: vi.fn(),
    onSelectPlayer: vi.fn(),
    ...overrides,
  };
  return { ...render(<MemoryRouter><SettingsProvider><PinnedSidebar {...defaults} /></SettingsProvider></MemoryRouter>), props: defaults };
}

describe('PinnedSidebar', () => {
  beforeEach(() => { vi.clearAllMocks(); });

  it('always renders (no open/close state)', () => {
    const { container } = renderPinned();
    expect(container.querySelector('[data-testid="pinned-sidebar"]')).toBeTruthy();
  });

  it('renders Songs nav link', () => {
    renderPinned();
    expect(screen.getByText('Songs')).toBeTruthy();
  });

  it('renders Suggestions and Statistics links when player is set', () => {
    renderPinned({ player: { accountId: 'a1', displayName: 'TestP' } });
    expect(screen.getByText('Suggestions')).toBeTruthy();
    expect(screen.getByText('Statistics')).toBeTruthy();
    expect(screen.getByText('Rivals')).toBeTruthy();
  });

  it('hides Suggestions and Statistics links when player is null', () => {
    renderPinned({ player: null });
    expect(screen.queryByText('Suggestions')).toBeNull();
    expect(screen.queryByText('Statistics')).toBeNull();
    expect(screen.queryByText('Rivals')).toBeNull();
  });

  it('shows player displayName when player is set', () => {
    renderPinned({ player: { accountId: 'a1', displayName: 'TestPlayer' } });
    expect(screen.getByText('TestPlayer')).toBeTruthy();
  });

  it('shows Select Player button when no player', () => {
    renderPinned({ player: null });
    expect(screen.getByText('Select Player Profile')).toBeTruthy();
  });

  it('calls onSelectPlayer when Select Player is clicked', () => {
    const { props } = renderPinned({ player: null });
    fireEvent.click(screen.getByText('Select Player Profile'));
    expect(props.onSelectPlayer).toHaveBeenCalled();
  });

  it('calls onDeselect when Deselect is clicked', () => {
    const { props } = renderPinned({ player: { accountId: 'a1', displayName: 'P' } });
    fireEvent.click(screen.getByText('Deselect'));
    expect(props.onDeselect).toHaveBeenCalled();
  });

  it('shows selected band members in the pinned sidebar', () => {
    renderPinned({
      player: null,
      selectedProfile: {
        type: 'band',
        bandId: 'band-1',
        bandType: 'Band_Trios',
        teamKey: 'p1:p2:p3',
        displayName: 'Player One + Player Two + Player Three',
        members: [
          { accountId: 'p1', displayName: 'Player One' },
          { accountId: 'p2', displayName: 'Player Two' },
          { accountId: 'p3', displayName: 'Player Three' },
        ],
      },
    });

    expect(screen.getByTestId('pinned-sidebar-band-profile')).toBeTruthy();
    expect(screen.getByText('Player One + Player Two + Player Three')).toBeTruthy();
    expect(screen.getByText('Player One')).toBeTruthy();
    expect(screen.getByText('Player Two')).toBeTruthy();
    expect(screen.getByText('Player Three')).toBeTruthy();
    expect(screen.getByText('Trios')).toBeTruthy();
    expect(screen.queryByText('Select Player Profile')).toBeNull();
  });

  it('deselects a selected band from the pinned sidebar', () => {
    const { props } = renderPinned({
      player: null,
      selectedProfile: {
        type: 'band',
        bandId: 'band-1',
        bandType: 'Band_Duets',
        teamKey: 'p1:p2',
        displayName: 'Player One + Player Two',
        members: [],
      },
    });

    fireEvent.click(screen.getByText('Deselect Band'));
    expect(props.onDeselect).toHaveBeenCalled();
  });

  it('renders Settings link', () => {
    renderPinned();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('renders no overlay', () => {
    const { container } = renderPinned();
    expect(container.querySelector('.overlay')).toBeNull();
  });
});

describe('PinnedSidebar — route-specific active styling', () => {
  it('shows active styling on songs route', () => {
    render(
      <MemoryRouter initialEntries={['/songs']}>
        <SettingsProvider>
        <PinnedSidebar player={null} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Songs');
    expect(link.closest('a')?.style.backgroundColor).toBeTruthy();
  });

  it('shows active styling on suggestions route', () => {
    render(
      <MemoryRouter initialEntries={['/suggestions']}>
        <SettingsProvider>
        <PinnedSidebar player={{ accountId: 'p1', displayName: 'P' } as any} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Suggestions');
    expect(link.closest('a')?.style.backgroundColor).toBeTruthy();
  });

  it('shows active styling on statistics route', () => {
    render(
      <MemoryRouter initialEntries={['/statistics']}>
        <SettingsProvider>
        <PinnedSidebar player={{ accountId: 'p1', displayName: 'P' } as any} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Statistics');
    expect(link.closest('a')?.style.backgroundColor).toBeTruthy();
  });

  it('shows active styling on settings route', () => {
    render(
      <MemoryRouter initialEntries={['/settings']}>
        <SettingsProvider>
        <PinnedSidebar player={null} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Settings');
    expect(link.closest('a')?.style.backgroundColor).toBeTruthy();
  });

  it('shows player name as link', () => {
    renderPinned({ player: { accountId: 'p1', displayName: 'TestP' } });
    expect(screen.getByText('TestP').closest('a')).toBeTruthy();
  });
});

describe('PinnedSidebar — wheel scroll forwarding', () => {
  it('forwards wheel events to scroll container', () => {
    const scrollBy = vi.fn();
    mockScrollRef.current = { scrollBy } as unknown as HTMLDivElement;
    const { container } = renderPinned();
    const aside = container.querySelector('[data-testid="pinned-sidebar"]')!;
    fireEvent.wheel(aside, { deltaY: 120, deltaX: 0 });
    expect(scrollBy).toHaveBeenCalledWith({ top: 120, left: 0 });
    mockScrollRef.current = null;
  });
});
