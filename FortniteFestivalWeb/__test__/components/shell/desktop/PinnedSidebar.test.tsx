import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import PinnedSidebar from '../../../../src/components/shell/desktop/PinnedSidebar';

vi.mock('../../../../src/components/shell/desktop/PinnedSidebar.module.css', () => ({
  default: {
    sidebar: 'sidebar', nav: 'nav',
    link: 'link', linkActive: 'linkActive', linkIcon: 'linkIcon',
    deselectBtn: 'deselectBtn', selectPlayerBtn: 'selectPlayerBtn',
  },
}));

function renderPinned(overrides: Partial<Parameters<typeof PinnedSidebar>[0]> = {}) {
  const defaults = {
    player: null,
    onDeselect: vi.fn(),
    onSelectPlayer: vi.fn(),
    ...overrides,
  };
  return { ...render(<MemoryRouter><PinnedSidebar {...defaults} /></MemoryRouter>), props: defaults };
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
  });

  it('hides Suggestions and Statistics links when player is null', () => {
    renderPinned({ player: null });
    expect(screen.queryByText('Suggestions')).toBeNull();
    expect(screen.queryByText('Statistics')).toBeNull();
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
        <PinnedSidebar player={null} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
      </MemoryRouter>,
    );
    const link = screen.getByText('Songs');
    expect(link.closest('a')?.className).toContain('linkActive');
  });

  it('shows active styling on suggestions route', () => {
    render(
      <MemoryRouter initialEntries={['/suggestions']}>
        <PinnedSidebar player={{ accountId: 'p1', displayName: 'P' } as any} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
      </MemoryRouter>,
    );
    const link = screen.getByText('Suggestions');
    expect(link.closest('a')?.className).toContain('linkActive');
  });

  it('shows active styling on statistics route', () => {
    render(
      <MemoryRouter initialEntries={['/statistics']}>
        <PinnedSidebar player={{ accountId: 'p1', displayName: 'P' } as any} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
      </MemoryRouter>,
    );
    const link = screen.getByText('Statistics');
    expect(link.closest('a')?.className).toContain('linkActive');
  });

  it('shows active styling on settings route', () => {
    render(
      <MemoryRouter initialEntries={['/settings']}>
        <PinnedSidebar player={null} onDeselect={vi.fn()} onSelectPlayer={vi.fn()} />
      </MemoryRouter>,
    );
    const link = screen.getByText('Settings');
    expect(link.closest('a')?.className).toContain('linkActive');
  });

  it('shows player name as link', () => {
    renderPinned({ player: { accountId: 'p1', displayName: 'TestP' } });
    expect(screen.getByText('TestP').closest('a')).toBeTruthy();
  });
});
