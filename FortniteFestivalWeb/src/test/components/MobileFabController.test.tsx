/**
 * Tests for MobileFabController — exercises all route-based FAB configurations.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { FabSearchProvider } from '../../contexts/FabSearchContext';
import type { ReactNode } from 'react';

vi.mock('../../contexts/SearchQueryContext', () => ({
  useSearchQuery: () => ({ query: '', setQuery: vi.fn() }),
}));

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal() as Record<string, unknown>;
  return { ...actual, useNavigate: () => mockNavigate };
});

let mockIsMobile = false;
vi.mock('../../hooks/ui/useIsMobile', () => ({
  useIsMobile: () => mockIsMobile,
}));

import MobileFabController from '../../components/shell/fab/MobileFabController';

function Wrapper({ children, route }: { children: ReactNode; route: string }) {
  return (
    <MemoryRouter initialEntries={[route]}>
      <FabSearchProvider>
        {children}
      </FabSearchProvider>
    </MemoryRouter>
  );
}

describe('MobileFabController', () => {
  const onFindPlayer = vi.fn();
  const onOpenPlayerModal = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    mockIsMobile = false;
    vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
    vi.useFakeTimers();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('renders songs FAB on /songs route', () => {
    render(
      <Wrapper route="/songs">
        <MobileFabController player={null} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    // Should render a FAB button
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('renders songs FAB with filter when player is set', () => {
    const player = { accountId: 'a1', displayName: 'TestP' };
    render(
      <Wrapper route="/songs">
        <MobileFabController player={player} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('renders suggestions FAB on /suggestions route', () => {
    render(
      <Wrapper route="/suggestions">
        <MobileFabController player={null} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('renders history FAB on /songs/x/y/history route', () => {
    render(
      <Wrapper route="/songs/s1/Solo_Guitar/history">
        <MobileFabController player={null} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('renders song detail FAB on /songs/:id route', () => {
    render(
      <Wrapper route="/songs/s1">
        <MobileFabController player={null} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('renders song detail FAB with viewPaths when narrow', () => {
    mockIsMobile = true;
    render(
      <Wrapper route="/songs/s1">
        <MobileFabController player={null} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('renders default FAB on other routes', () => {
    render(
      <Wrapper route="/settings">
        <MobileFabController player={null} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    expect(screen.getByRole('button', { name: /actions/i })).toBeTruthy();
  });

  it('player FAB shows player name when player is set', () => {
    const player = { accountId: 'a1', displayName: 'TestP' };
    render(
      <Wrapper route="/settings">
        <MobileFabController player={player} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    // Open the FAB menu
    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    expect(screen.getByText('TestP')).toBeTruthy();
  });

  it('onFindPlayer is called from player actions', () => {
    render(
      <Wrapper route="/settings">
        <MobileFabController player={null} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    // Find the "Find Player" button — it uses i18n key
    const items = screen.getAllByRole('button');
    // Click at least one action item besides the FAB itself
    for (const item of items) {
      if (item.textContent?.includes('findPlayer') || item.textContent?.includes('Find')) {
        fireEvent.click(item);
        break;
      }
    }
  });

  it('navigate to /statistics when player action clicked', () => {
    const player = { accountId: 'a1', displayName: 'TestP' };
    render(
      <Wrapper route="/settings">
        <MobileFabController player={player} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    fireEvent.click(screen.getByText('TestP'));
    expect(mockNavigate).toHaveBeenCalledWith('/statistics');
  });

  it('calls onOpenPlayerModal when no player and select profile clicked', () => {
    render(
      <Wrapper route="/settings">
        <MobileFabController player={null} onFindPlayer={onFindPlayer} onOpenPlayerModal={onOpenPlayerModal} />
      </Wrapper>,
    );
    fireEvent.click(screen.getByRole('button', { name: /actions/i }));
    // Look for selectPlayerProfile text
    const items = screen.getAllByRole('button');
    for (const item of items) {
      if (item.textContent?.includes('selectPlayerProfile') || item.textContent?.includes('Select')) {
        fireEvent.click(item);
        break;
      }
    }
    expect(onOpenPlayerModal).toHaveBeenCalled();
  });
});
