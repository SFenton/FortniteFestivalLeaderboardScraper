import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import Sidebar from '../../../../src/components/shell/desktop/Sidebar';

vi.mock('../../../../src/components/shell/desktop/Sidebar.module.css', () => ({
  default: {
    overlay: 'overlay', sidebar: 'sidebar', sidebarHeader: 'sidebarHeader',
    brand: 'brand', sidebarNav: 'sidebarNav', sidebarLink: 'sidebarLink',
    sidebarLinkActive: 'sidebarLinkActive', sidebarFooter: 'sidebarFooter',
    sidebarPlayerRow: 'sidebarPlayerRow', playerLink: 'playerLink',
    deselectBtn: 'deselectBtn', selectPlayerBtn: 'selectPlayerBtn',
    profileCircle: 'profileCircle', profileCircleEmpty: 'profileCircleEmpty',
  },
}));

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
  return { ...render(<MemoryRouter><Sidebar {...defaults} /></MemoryRouter>), props: defaults };
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
  });

  it('hides Suggestions and Statistics links when player is null', () => {
    renderSidebar({ player: null });
    expect(screen.queryByText('Suggestions')).toBeNull();
    expect(screen.queryByText('Statistics')).toBeNull();
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
    const overlay = container.querySelector('.overlay');
    if (overlay) fireEvent.click(overlay);
    expect(props.onClose).toHaveBeenCalled();
  });

  it('calls onClose on outside mousedown', () => {
    const { props } = renderSidebar();
    fireEvent.mouseDown(document);
    expect(props.onClose).toHaveBeenCalled();
  });

  it('unmounts after transition ends while closed', () => {
    const { container, rerender } = render(
      <MemoryRouter><Sidebar player={null} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} /></MemoryRouter>,
    );
    // Close it
    rerender(
      <MemoryRouter><Sidebar player={null} open={false} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} /></MemoryRouter>,
    );
    const sidebar = container.querySelector('.sidebar');
    if (sidebar) fireEvent.transitionEnd(sidebar);
    // After transition, should be unmounted
    expect(container.querySelector('.sidebar')).toBeNull();
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
        <Sidebar player={null} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
      </MemoryRouter>,
    );
    const settingsLink = screen.getByText('Settings');
    expect(settingsLink.closest('a')?.className).toContain('sidebarLinkActive');
  });

  it('shows player name as link', () => {
    renderSidebar({ player: { accountId: 'p1', displayName: 'TestP' } });
    expect(screen.getByText('TestP').closest('a')).toBeTruthy();
  });

  it('renders with active styling on suggestions route', () => {
    render(
      <MemoryRouter initialEntries={['/suggestions']}>
        <Sidebar player={{ accountId: 'p1', displayName: 'P' } as any} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
      </MemoryRouter>,
    );
    const link = screen.getByText('Suggestions');
    expect(link.closest('a')?.className).toContain('sidebarLinkActive');
  });

  it('renders with active styling on statistics route', () => {
    render(
      <MemoryRouter initialEntries={['/statistics']}>
        <Sidebar player={{ accountId: 'p1', displayName: 'P' } as any} open={true} onClose={vi.fn()} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
      </MemoryRouter>,
    );
    const link = screen.getByText('Statistics');
    expect(link.closest('a')?.className).toContain('sidebarLinkActive');
  });
});
