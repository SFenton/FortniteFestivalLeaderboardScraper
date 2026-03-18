import { describe, it, expect, vi } from 'vitest';
import { render, screen, fireEvent } from '@testing-library/react';

import RouteErrorFallback from '../../../src/components/page/RouteErrorFallback';

describe('RouteErrorFallback', () => {
  it('renders error message', () => {
    render(<RouteErrorFallback />);
    expect(screen.getByText('Something went wrong')).toBeTruthy();
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
});
