import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../../../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => ({ rivals: true, compete: true, leaderboards: true, firstRun: true }),
}));

import Sidebar from '../../../../src/components/shell/desktop/Sidebar';
import { SettingsProvider } from '../../../../src/contexts/SettingsContext';

beforeEach(() => {
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

describe('Sidebar', () => {
  it('renders nothing when not open', () => {
    const { container } = renderSidebar({ open: false });
    expect(container.innerHTML).toBe('');
  });

  it('renders Songs nav link when open', () => {
    renderSidebar();
    expect(screen.getByText('Songs')).toBeTruthy();
  });

  it('renders Suggestions and Statistics links when player is set', () => {
    renderSidebar({ player: { accountId: 'a1', displayName: 'TestP' } });
    expect(screen.getByText('Suggestions')).toBeTruthy();
    expect(screen.getByText('Statistics')).toBeTruthy();
    expect(screen.getByText('Rivals')).toBeTruthy();
  });

  it('hides Suggestions and Statistics links when player is null', () => {
    renderSidebar({ player: null });
    expect(screen.queryByText('Suggestions')).toBeNull();
    expect(screen.queryByText('Statistics')).toBeNull();
    expect(screen.queryByText('Rivals')).toBeNull();
  });

  it('shows player displayName when player is set', () => {
    renderSidebar({ player: { accountId: 'a1', displayName: 'TestPlayer' } });
    expect(screen.getByText('TestPlayer')).toBeTruthy();
  });

  it('shows Select Player button when no player', () => {
    renderSidebar({ player: null });
    expect(screen.getByText('Select Player Profile')).toBeTruthy();
  });

  it('calls onSelectPlayer when Select Player is clicked', () => {
    const { props } = renderSidebar({ player: null });
    fireEvent.click(screen.getByText('Select Player Profile'));
    expect(props.onSelectPlayer).toHaveBeenCalled();
  });

  it('calls onDeselect when Deselect is clicked', () => {
    const { props } = renderSidebar({ player: { accountId: 'a1', displayName: 'P' } });
    fireEvent.click(screen.getByText('Deselect'));
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
});

describe('Sidebar — route-specific active styling', () => {
  it('calls onClose when nav link is clicked', () => {
    const { props } = renderSidebar({ player: { accountId: 'p1', displayName: 'P' } });
    fireEvent.click(screen.getByText('Songs'));
    expect(props.onClose).toHaveBeenCalled();
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
    // Active link has border-left style with accent purple
    expect(settingsLink.closest('a')?.style.borderLeft).toContain('solid');
  });

  it('shows player name as link', () => {
    renderSidebar({ player: { accountId: 'p1', displayName: 'TestP' } });
    expect(screen.getByText('TestP').closest('a')).toBeTruthy();
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
    expect(link.closest('a')?.style.borderLeft).toContain('solid');
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
    expect(link.closest('a')?.style.borderLeft).toContain('solid');
  });
});
