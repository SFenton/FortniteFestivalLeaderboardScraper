import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom';
import { RequirePlayer, RequireSelection } from '../../../src/components/page/RouteGuards';

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{`${location.pathname}${location.search}`}</div>;
}

function renderGuard(guard: React.ReactNode, initialEntry = '/guarded?source=test') {
  return render(
    <MemoryRouter initialEntries={[initialEntry]}>
      <Routes>
        <Route path="/guarded" element={guard} />
        <Route path="/songs" element={<LocationProbe />} />
      </Routes>
    </MemoryRouter>,
  );
}

describe('RouteGuards', () => {
  it('renders player-only content when a player is available', () => {
    renderGuard(
      <RequirePlayer hasPlayer>
        <div>Player content</div>
      </RequirePlayer>,
    );
    expect(screen.getByText('Player content')).toBeDefined();
  });

  it('redirects player-only content to Songs without rendering children', () => {
    renderGuard(
      <RequirePlayer hasPlayer={false}>
        <div>Player content</div>
      </RequirePlayer>,
    );
    expect(screen.queryByText('Player content')).toBeNull();
    expect(screen.getByTestId('location')).toHaveTextContent('/songs');
  });

  it('accepts either selected profile type and rejects no selection', () => {
    const selected = renderGuard(
      <RequireSelection hasSelection>
        <div>Selected content</div>
      </RequireSelection>,
    );
    expect(screen.getByText('Selected content')).toBeDefined();
    selected.unmount();

    renderGuard(
      <RequireSelection hasSelection={false}>
        <div>Selected content</div>
      </RequireSelection>,
    );
    expect(screen.queryByText('Selected content')).toBeNull();
    expect(screen.getByTestId('location')).toHaveTextContent('/songs');
  });
});
