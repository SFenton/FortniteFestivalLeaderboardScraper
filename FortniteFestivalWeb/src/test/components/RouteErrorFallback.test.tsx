import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';

import RouteErrorFallback from '../../components/page/RouteErrorFallback';

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
});
