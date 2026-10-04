import React from 'react';
import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom';
import RouteBoundary from '../../../src/components/page/RouteBoundary';

function ThrowingChild(): React.ReactNode {
  throw new Error('route failed');
}

function renderBoundary(children: React.ReactNode) {
  return render(
    <MemoryRouter>
      <RouteBoundary>{children}</RouteBoundary>
    </MemoryRouter>,
  );
}

function RecoveryHarness() {
  const location = useLocation();
  const navigate = useNavigate();
  return (
    <>
      <button type="button" onClick={() => navigate('/songs')}>Navigate to Songs</button>
      <RouteBoundary>
        {location.pathname === '/broken' ? <ThrowingChild /> : <div>Songs content</div>}
      </RouteBoundary>
    </>
  );
}

let ownerMounts = 0;
function MountCounter() {
  const [mountId] = React.useState(() => ++ownerMounts);
  return <div data-testid="mount-id">{mountId}</div>;
}

function ModalRouteHarness() {
  const navigate = useNavigate();
  return (
    <>
      <button type="button" onClick={() => navigate('/settings/privacy')}>Open privacy</button>
      <button type="button" onClick={() => navigate('/settings')}>Close privacy</button>
      <RouteBoundary><MountCounter /></RouteBoundary>
    </>
  );
}

describe('RouteBoundary', () => {
  it('renders route content', () => {
    renderBoundary(<div>Route content</div>);
    expect(screen.getByText('Route content')).toBeDefined();
  });

  it('renders the standard route fallback with one heading', () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    renderBoundary(<ThrowingChild />);

    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Something went wrong');
    expect(screen.getByRole('link', { name: 'Go to Songs' })).toHaveAttribute('href', '#/songs');
    consoleSpy.mockRestore();
  });

  it('resets the error state when the route pathname changes', () => {
    const consoleSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
    render(
      <MemoryRouter initialEntries={['/broken']}>
        <RecoveryHarness />
      </MemoryRouter>,
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Something went wrong');

    fireEvent.click(screen.getByRole('button', { name: 'Navigate to Songs' }));

    expect(screen.getByText('Songs content')).toBeDefined();
    consoleSpy.mockRestore();
  });

  it('keeps the page owner mounted while its modal route opens and closes', () => {
    ownerMounts = 0;
    render(
      <MemoryRouter initialEntries={['/settings']}>
        <ModalRouteHarness />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('mount-id')).toHaveTextContent('1');

    fireEvent.click(screen.getByRole('button', { name: 'Open privacy' }));
    expect(screen.getByTestId('mount-id')).toHaveTextContent('1');

    fireEvent.click(screen.getByRole('button', { name: 'Close privacy' }));
    expect(screen.getByTestId('mount-id')).toHaveTextContent('1');
  });
});
