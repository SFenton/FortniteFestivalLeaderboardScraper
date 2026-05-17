import { describe, it, expect, vi, beforeAll, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

const featureFlagsMock = vi.hoisted(() => ({
  value: { compete: true, leaderboards: true, difficulty: true, playerBands: true, experimentalRanks: true, appManual: true },
}));

vi.mock('../../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => featureFlagsMock.value,
}));

import Sidebar from '../../../../src/components/shell/desktop/Sidebar';
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

beforeEach(() => {
  featureFlagsMock.value = { compete: true, leaderboards: true, difficulty: true, playerBands: true, experimentalRanks: true, appManual: true };
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
});

afterEach(() => {
  vi.restoreAllMocks();
});

function renderSidebar(overrides: Partial<Parameters<typeof Sidebar>[0]> = {}) {
  const defaults = {
    player: null,
    open: true,
    onClose: vi.fn(),
    onDeselect: vi.fn(),
    onSelectPlayer: vi.fn(),
    ...overrides,
  };
  return { ...render(<MemoryRouter><SettingsProvider><Sidebar {...defaults} /></SettingsProvider></MemoryRouter>), props: defaults };
}

function dispatchPointer(target: Element, type: string, props: Partial<PointerEvent> = {}) {
  const event = new Event(type, { bubbles: true, cancelable: true }) as PointerEvent;
  Object.defineProperties(event, {
    pointerId: { value: props.pointerId ?? 1 },
    pointerType: { value: props.pointerType ?? 'touch' },
    isPrimary: { value: props.isPrimary ?? true },
    button: { value: props.button ?? 0 },
    clientX: { value: props.clientX ?? 0 },
    clientY: { value: props.clientY ?? 0 },
    timeStamp: { value: props.timeStamp ?? 0 },
  });
  fireEvent(target, event);
  return event;
}

function dispatchClick(target: Element, props: { clientX?: number; clientY?: number; timeStamp?: number } = {}) {
  const event = new MouseEvent('click', {
    bubbles: true,
    cancelable: true,
    clientX: props.clientX ?? 0,
    clientY: props.clientY ?? 0,
  });
  Object.defineProperty(event, 'timeStamp', { value: props.timeStamp ?? 0 });
  fireEvent(target, event);
  return event;
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

function expectSidebarLinkActive(link: HTMLAnchorElement | null) {
  expect(link?.style.backgroundColor).toBe('rgb(124, 58, 237)');
  expect(link?.style.borderLeft).toContain('3px solid');
}

function expectSidebarLinkInactive(link: HTMLAnchorElement | null) {
  expect(link?.style.backgroundColor).toBe('transparent');
  expect(link?.style.borderLeft).not.toContain('3px solid');
}

describe('Sidebar', () => {
  it('renders nothing when not open', () => {
    const { container } = renderSidebar({ open: false });
    expect(container.innerHTML).toBe('');
  });

  it('renders Songs nav link when open', () => {
    renderSidebar();
    expect(screen.getByText('Songs')).toBeTruthy();
  });

  it('allows overlay and drawer hit-testing immediately before the entrance frame runs', () => {
    vi.mocked(window.requestAnimationFrame).mockImplementation(() => 1);
    const { container } = renderSidebar();
    const allDivs = Array.from(container.querySelectorAll('div'));
    const overlay = allDivs.find(el => el.style.zIndex === '200' && el.style.position === 'fixed') as HTMLElement;
    const drawer = allDivs.find(el => el.style.transform.includes('translateX')) as HTMLElement;

    expect(overlay.style.pointerEvents).toBe('auto');
    expect(drawer.style.pointerEvents).toBe('auto');
  });

  it('keeps sidebar header and footer inside iOS safe areas', () => {
    renderSidebar();

    const header = screen.getByText('Festival Score Tracker').closest('div');
    const footer = screen.getByText('Settings').closest('div');

    expect(header?.style.padding).toContain('var(--sat');
    expect(footer?.style.padding).toContain('var(--sab');
  });

  it('renders Suggestions and Statistics links when player is set', () => {
    renderSidebar({ player: { accountId: 'a1', displayName: 'TestP' } });
    expect(screen.getByText('Suggestions')).toBeTruthy();
    expect(screen.getByText('Statistics')).toBeTruthy();
    expect(screen.getByText('Rivals')).toBeTruthy();
  });

  it('hides Suggestions and Statistics links when no player or band is selected', () => {
    renderSidebar({ player: null, selectedProfile: null });
    expect(screen.queryByText('Suggestions')).toBeNull();
    expect(screen.queryByText('Statistics')).toBeNull();
    expect(screen.queryByText('Rivals')).toBeNull();
  });

  it('renders Suggestions and Statistics links when a band is selected without a player', () => {
    renderSidebar({ player: null, selectedProfile: selectedBandProfile });
    const suggestionsLink = screen.getByText('Suggestions').closest('a');
    const statisticsLink = screen.getByText('Statistics').closest('a');

    expect(screen.queryByText('Rivals')).toBeNull();
    expect(suggestionsLink?.getAttribute('href')).toBe('/suggestions');
    expect(statisticsLink?.getAttribute('href')).toBe('/statistics');
  });

  it('shows player displayName when player is set', () => {
    renderSidebar({ player: { accountId: 'a1', displayName: 'TestPlayer' } });
    expect(screen.getAllByText('TestPlayer').length).toBeGreaterThan(0);
  });

  it('shows Select Player button when no player', () => {
    renderSidebar({ player: null });
    expect(screen.getByRole('button', { name: /^select/i })).toBeTruthy();
  });

  it('calls onSelectPlayer when Select Player is clicked', () => {
    const { props } = renderSidebar({ player: null });
    fireEvent.click(screen.getByRole('button', { name: /^select/i }));
    expect(props.onSelectPlayer).toHaveBeenCalled();
  });

  it('calls onDeselect when Deselect is clicked', () => {
    const { props } = renderSidebar({ player: { accountId: 'a1', displayName: 'P' } });
    fireEvent.click(screen.getByText('Deselect'));
    expect(props.onDeselect).toHaveBeenCalled();
  });

  it('shows selected band members in the sidebar footer', () => {
    renderSidebar({
      player: null,
      selectedProfile: selectedBandProfile,
    });

    expect(screen.getByTestId('sidebar-band-profile')).toBeTruthy();
    expect(screen.getAllByText('Player One + Player Two').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Player One').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Player Two').length).toBeGreaterThan(0);
    expect(screen.queryByRole('button', { name: /^select/i })).toBeNull();
  });

  it('deselects a selected band from the sidebar footer', () => {
    const { props } = renderSidebar({
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

  it('calls onClose when overlay is clicked', () => {
    const { props, container } = renderSidebar();
    // Sidebar renders: <Fragment><div (overlay)>...<div (sidebar)>...</Fragment>
    // Find the overlay: it's the first div with position:fixed and z-index:200
    const allDivs = container.querySelectorAll('div');
    const overlay = Array.from(allDivs).find(el => el.style.zIndex === '200' && el.style.position === 'fixed');
    expect(overlay).toBeTruthy();
    fireEvent.click(overlay!);
    expect(props.onClose).toHaveBeenCalled();
  });

  it('dismisses from touch pointerup on the overlay without double firing on click', () => {
    const { props, container } = renderSidebar();
    const allDivs = container.querySelectorAll('div');
    const overlay = Array.from(allDivs).find(el => el.style.zIndex === '200' && el.style.position === 'fixed') as HTMLElement;

    fireEvent.pointerDown(overlay, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 340, clientY: 320 });
    fireEvent.pointerUp(overlay, { pointerId: 1, pointerType: 'touch', button: 0, clientX: 340, clientY: 320 });

    expect(props.onClose).toHaveBeenCalledTimes(1);

    fireEvent.click(overlay);
    expect(props.onClose).toHaveBeenCalledTimes(1);
  });

  it('dismisses immediately from a touch pointerup on a nav link', () => {
    const { props } = renderSidebar({ player: { accountId: 'a1', displayName: 'P' } });
    const suggestionsLink = screen.getByText('Suggestions').closest('a')!;

    dispatchPointer(suggestionsLink, 'pointerdown', { pointerId: 1, pointerType: 'touch', button: 0, clientX: 54, clientY: 208, timeStamp: 10 });
    dispatchPointer(suggestionsLink, 'pointerup', { pointerId: 1, pointerType: 'touch', button: 0, clientX: 55, clientY: 209, timeStamp: 20 });

    expect(props.onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByText('Festival Score Tracker')).toBeNull();
    dispatchClick(document.body, { clientX: 55, clientY: 209, timeStamp: 80 });
  });

  it('suppresses a compatibility click retargeted beneath a closing sidebar nav link', () => {
    const onUnderlyingPress = vi.fn();
    const onClose = vi.fn();
    render(
      <MemoryRouter>
        <SettingsProvider>
          <button type="button" data-testid="underlying" onClick={onUnderlyingPress}>Underlying</button>
          <Sidebar player={{ accountId: 'a1', displayName: 'P' }} open={true} onClose={onClose} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const suggestionsLink = screen.getByText('Suggestions').closest('a')!;

    dispatchPointer(suggestionsLink, 'pointerdown', { clientX: 55, clientY: 209, timeStamp: 10 });
    dispatchPointer(suggestionsLink, 'pointerup', { clientX: 55, clientY: 209, timeStamp: 20 });
    const syntheticClick = dispatchClick(screen.getByTestId('underlying'), { clientX: 55, clientY: 209, timeStamp: 80 });

    expect(onClose).toHaveBeenCalledTimes(1);
    expect(syntheticClick.defaultPrevented).toBe(true);
    expect(onUnderlyingPress).not.toHaveBeenCalled();
  });

  it('allows an immediate click outside the sidebar nav compatibility-click radius', () => {
    const onUnderlyingPress = vi.fn();
    render(
      <MemoryRouter>
        <SettingsProvider>
          <button type="button" data-testid="underlying" onClick={onUnderlyingPress}>Underlying</button>
          <Sidebar player={{ accountId: 'a1', displayName: 'P' }} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const suggestionsLink = screen.getByText('Suggestions').closest('a')!;

    dispatchPointer(suggestionsLink, 'pointerdown', { clientX: 55, clientY: 209, timeStamp: 10 });
    dispatchPointer(suggestionsLink, 'pointerup', { clientX: 55, clientY: 209, timeStamp: 20 });
    const distantClick = dispatchClick(screen.getByTestId('underlying'), { clientX: 110, clientY: 209, timeStamp: 80 });

    expect(distantClick.defaultPrevented).toBe(false);
    expect(onUnderlyingPress).toHaveBeenCalledTimes(1);
  });

  it('allows a later real click after sidebar nav compatibility-click suppression expires', () => {
    const onUnderlyingPress = vi.fn();
    render(
      <MemoryRouter>
        <SettingsProvider>
          <button type="button" data-testid="underlying" onClick={onUnderlyingPress}>Underlying</button>
          <Sidebar player={{ accountId: 'a1', displayName: 'P' }} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const suggestionsLink = screen.getByText('Suggestions').closest('a')!;

    dispatchPointer(suggestionsLink, 'pointerdown', { clientX: 55, clientY: 209, timeStamp: 10 });
    dispatchPointer(suggestionsLink, 'pointerup', { clientX: 55, clientY: 209, timeStamp: 20 });
    const laterClick = dispatchClick(screen.getByTestId('underlying'), { clientX: 55, clientY: 209, timeStamp: 900 });

    expect(laterClick.defaultPrevented).toBe(false);
    expect(onUnderlyingPress).toHaveBeenCalledTimes(1);
  });

  it('calls onClose on outside mousedown', () => {
    const { props } = renderSidebar();
    fireEvent.mouseDown(document);
    expect(props.onClose).toHaveBeenCalled();
  });

  it('unmounts after transition ends while closed', () => {
    const { rerender } = render(
      <MemoryRouter><SettingsProvider><Sidebar player={null} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} /></SettingsProvider></MemoryRouter>,
    );
    // Close it
    rerender(
      <MemoryRouter><SettingsProvider><Sidebar player={null} open={false} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} /></SettingsProvider></MemoryRouter>,
    );
    // Sidebar is the second child (after overlay) — find via text content
    const sidebar = screen.queryByText('Festival Score Tracker')?.closest('div[style]');
    if (sidebar) fireEvent.transitionEnd(sidebar);
    // After transition, should be unmounted
    expect(screen.queryByText('Festival Score Tracker')).toBeNull();
  });

  it('renders Settings link', () => {
    renderSidebar();
    expect(screen.getByText('Settings')).toBeTruthy();
  });

  it('renders Manual link between profile selection and Settings', () => {
    const { container } = renderSidebar({ player: null });
    const text = container.textContent ?? '';

    expect(screen.getByText('App Manual').closest('a')?.getAttribute('href')).toBe('/manual');
    expect(text.indexOf('Select Profile')).toBeLessThan(text.indexOf('App Manual'));
    expect(text.indexOf('App Manual')).toBeLessThan(text.indexOf('Settings'));
  });

  it('hides Manual link when the App Manual feature is disabled', () => {
    featureFlagsMock.value = { ...featureFlagsMock.value, appManual: false };
    renderSidebar({ player: null });

    expect(screen.queryByText('App Manual')).toBeNull();
    expect(screen.getByText('Settings')).toBeTruthy();
  });
});

describe('Sidebar — route-specific active styling', () => {
  it('calls onClose when nav link is clicked', () => {
    const { props } = renderSidebar({ player: { accountId: 'p1', displayName: 'P' } });
    fireEvent.click(screen.getByText('Songs'));
    expect(props.onClose).toHaveBeenCalled();
    expect(screen.queryByText('Festival Score Tracker')).toBeNull();
  });

  it('shows active styling on settings route', () => {
    render(
      <MemoryRouter initialEntries={['/settings']}>
        <SettingsProvider>
        <Sidebar player={null} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const settingsLink = screen.getByText('Settings');
    expectSidebarLinkActive(settingsLink.closest('a'));
  });

  it('shows player name as link', () => {
    renderSidebar({ player: { accountId: 'p1', displayName: 'TestP' } });
    expect(screen.getAllByText('TestP')[0]?.closest('a')).toBeTruthy();
  });

  it('renders with active styling on suggestions route', () => {
    render(
      <MemoryRouter initialEntries={['/suggestions']}>
        <SettingsProvider>
        <Sidebar player={{ accountId: 'p1', displayName: 'P' } as any} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Suggestions');
    expectSidebarLinkActive(link.closest('a'));
  });

  it('renders with active styling on statistics route', () => {
    render(
      <MemoryRouter initialEntries={['/statistics']}>
        <SettingsProvider>
        <Sidebar player={{ accountId: 'p1', displayName: 'P' } as any} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Statistics');
    expectSidebarLinkActive(link.closest('a'));
  });

  it('does not render Statistics as active on a clean band detail route', () => {
    render(
      <MemoryRouter initialEntries={['/bands/band-1?bandType=Band_Duets&teamKey=p1%3Ap2&names=Player%20One%20%2B%20Player%20Two']}>
        <SettingsProvider>
        <Sidebar player={null} selectedProfile={selectedBandProfile} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Statistics');
    expectSidebarLinkInactive(link.closest('a'));
  });

  it('does not render Statistics as active on a clean player detail route', () => {
    render(
      <MemoryRouter initialEntries={['/player/p2']}>
        <SettingsProvider>
        <Sidebar player={{ accountId: 'p1', displayName: 'P' } as any} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
        </SettingsProvider>
      </MemoryRouter>,
    );
    const link = screen.getByText('Statistics');
    expectSidebarLinkInactive(link.closest('a'));
  });

  it('links the selected band footer profile to statistics', () => {
    renderSidebar({ player: null, selectedProfile: selectedBandProfile });
    expect(screen.getByTestId('sidebar-band-profile').querySelector('a')?.getAttribute('href')).toBe('/statistics');
  });
});
