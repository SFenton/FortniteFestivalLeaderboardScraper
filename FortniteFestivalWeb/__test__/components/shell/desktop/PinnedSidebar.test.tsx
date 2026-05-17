import { describe, it, expect, vi, beforeAll, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

const featureFlagsMock = vi.hoisted(() => ({
  value: { compete: true, leaderboards: true, difficulty: true, playerBands: true, experimentalRanks: true, appManual: true },
}));

vi.mock('../../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => featureFlagsMock.value,
}));

const mockScrollRef = { current: null as HTMLDivElement | null };
vi.mock('../../../../src/contexts/ScrollContainerContext', () => ({
  useScrollContainer: () => mockScrollRef,
}));

import PinnedSidebar from '../../../../src/components/shell/desktop/PinnedSidebar';
import { SettingsProvider } from '../../../../src/contexts/SettingsContext';

beforeAll(() => {
  if (typeof Range !== 'undefined') {
    Object.defineProperty(Range.prototype, 'getBoundingClientRect', {
      configurable: true,
      value: () => ({ top: 0, right: 120, bottom: 16, left: 0, width: 120, height: 16, x: 0, y: 0, toJSON() { return this; } }),
    });
    Object.defineProperty(Range.prototype, 'getClientRects', {
      configurable: true,
      value: () => [] as unknown as DOMRectList,
    });
  }
});

function renderPinned(overrides: Partial<Parameters<typeof PinnedSidebar>[0]> = {}) {
  const defaults = {
    player: null,
    onDeselect: vi.fn(),
    onSelectPlayer: vi.fn(),
    ...overrides,
  };
  return { ...render(<MemoryRouter><SettingsProvider><PinnedSidebar {...defaults} /></SettingsProvider></MemoryRouter>), props: defaults };
}

const selectedBandProfile = {
  type: 'band' as const,
  bandId: 'band-1',
  bandType: 'Band_Duets' as const,
  teamKey: 'p1:p2',
  displayName: 'Player One + Player Two',
  members: [
    { accountId: 'p1', displayName: 'Player One' },
    { accountId: 'p2', displayName: 'Player Two' },
  ],
};

function expectPinnedLinkActive(link: HTMLAnchorElement | null) {
  expect(link?.style.backgroundColor).not.toBe('transparent');
}

function expectPinnedLinkInactive(link: HTMLAnchorElement | null) {
  expect(link?.style.backgroundColor).toBe('transparent');
}

describe('PinnedSidebar', () => {
  beforeEach(() => {
    featureFlagsMock.value = { compete: true, leaderboards: true, difficulty: true, playerBands: true, experimentalRanks: true, appManual: true };
    vi.clearAllMocks();
  });

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

  it('hides Suggestions and Statistics links when no player or band is selected', () => {
    renderPinned({ player: null, selectedProfile: null });
    expect(screen.queryByText('Suggestions')).toBeNull();
    expect(screen.queryByText('Statistics')).toBeNull();
    expect(screen.queryByText('Rivals')).toBeNull();
  });

  it('renders Suggestions and Statistics links when a band is selected without a player', () => {
    renderPinned({ player: null, selectedProfile: selectedBandProfile });
    const suggestionsLink = screen.getByText('Suggestions').closest('a');
    const statisticsLink = screen.getByText('Statistics').closest('a');

    expect(screen.queryByText('Rivals')).toBeNull();
    expect(suggestionsLink?.getAttribute('href')).toBe('/suggestions');
    expect(statisticsLink?.getAttribute('href')).toBe('/statistics');
  });

  it('shows player displayName when player is set', () => {
    renderPinned({ player: { accountId: 'a1', displayName: 'TestPlayer' } });
    expect(screen.getAllByText('TestPlayer').length).toBeGreaterThan(0);
  });

  it('shows Select Player button when no player', () => {
    renderPinned({ player: null });
    expect(screen.getByRole('button', { name: /^select/i })).toBeTruthy();
  });

  it('calls onSelectPlayer when Select Player is clicked', () => {
    const { props } = renderPinned({ player: null });
    fireEvent.click(screen.getByRole('button', { name: /^select/i }));
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
  expect(screen.getAllByText('Player One + Player Two + Player Three').length).toBeGreaterThan(0);
  expect(screen.getAllByText('Player One').length).toBeGreaterThan(0);
  expect(screen.getAllByText('Player Two').length).toBeGreaterThan(0);
  expect(screen.getAllByText('Player Three').length).toBeGreaterThan(0);
    expect(screen.getByText('Trios')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /^select/i })).toBeNull();
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

  it('renders Manual link between profile selection and Settings', () => {
    const { container } = renderPinned({ player: null });
    const text = container.textContent ?? '';

    expect(screen.getByText('App Manual').closest('a')?.getAttribute('href')).toBe('/manual');
    expect(text.indexOf('Select Profile')).toBeLessThan(text.indexOf('App Manual'));
    expect(text.indexOf('App Manual')).toBeLessThan(text.indexOf('Settings'));
  });

  it('hides Manual link when the App Manual feature is disabled', () => {
    featureFlagsMock.value = { ...featureFlagsMock.value, appManual: false };
    renderPinned({ player: null });

    expect(screen.queryByText('App Manual')).toBeNull();
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
    expectPinnedLinkActive(link.closest('a'));
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
    expectPinnedLinkActive(link.closest('a'));
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
    expectPinnedLinkActive(link.closest('a'));
  });

  it('does not show Statistics as active on a clean band detail route', () => {
    render(
      <MemoryRouter initialEntries={['/bands/band-1?bandType=Band_Duets&teamKey=p1%3Ap2&names=Player%20One%20%2B%20Player%20Two']}>
        <SettingsProvider>
        <PinnedSidebar player={null} selectedProfile={selectedBandProfile} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Statistics');
    expectPinnedLinkInactive(link.closest('a'));
  });

  it('does not show Statistics as active on a clean player detail route', () => {
    render(
      <MemoryRouter initialEntries={['/player/p2']}>
        <SettingsProvider>
        <PinnedSidebar player={{ accountId: 'p1', displayName: 'P' } as any} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Statistics');
    expectPinnedLinkInactive(link.closest('a'));
  });

  it('links the selected band profile to statistics', () => {
    renderPinned({ selectedProfile: selectedBandProfile });
    expect(screen.getByTestId('pinned-sidebar-band-profile').querySelector('a')?.getAttribute('href')).toBe('/statistics');
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
    expectPinnedLinkActive(link.closest('a'));
  });

  it('shows player name as link', () => {
    renderPinned({ player: { accountId: 'p1', displayName: 'TestP' } });
    expect(screen.getAllByText('TestP')[0]?.closest('a')).toBeTruthy();
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
