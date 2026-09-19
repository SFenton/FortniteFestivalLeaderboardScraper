import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';

import RouteErrorFallback from '../../../src/components/page/RouteErrorFallback';
import {
  PageReadyProvider,
  usePageReady,
} from '../../../src/contexts/PageReadyContext';

function ReadyProbe() {
  return <output data-testid="page-ready">{String(usePageReady())}</output>;
}

describe('RouteErrorFallback', () => {
  it('renders error message', () => {
    render(<RouteErrorFallback />);
    expect(screen.getByRole('heading', { level: 1, name: 'Something went wrong' })).toBeTruthy();
  });

  it('renders Go to Songs link', () => {
    render(<RouteErrorFallback />);
    const link = screen.getByText('Go to Songs');
    expect(link).toBeTruthy();
    expect(link.getAttribute('href')).toBe('#/songs');
  });

  it('renders Reload button', () => {
    render(<RouteErrorFallback />);
    expect(screen.getByText('Reload')).toBeTruthy();
  });

  it('reload button calls window.location.reload', () => {
    const reloadMock = vi.fn();
    Object.defineProperty(window, 'location', {
      value: { ...window.location, reload: reloadMock },
      writable: true,
    });
    render(<RouteErrorFallback />);
    fireEvent.click(screen.getByText('Reload'));
    expect(reloadMock).toHaveBeenCalled();
  });

  it('publishes terminal readiness so route failures cannot deadlock startup', async () => {
    render(
      <MemoryRouter>
        <PageReadyProvider>
          <RouteErrorFallback />
          <ReadyProbe />
        </PageReadyProvider>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByTestId('page-ready')).toHaveTextContent('true'));
  });
});
