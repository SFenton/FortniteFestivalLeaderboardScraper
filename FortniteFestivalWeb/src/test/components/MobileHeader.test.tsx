import { describe, it, expect } from 'vitest';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import MobileHeader from '../../components/shell/mobile/MobileHeader';

function renderWithRouter(ui: React.ReactElement, { route = '/songs' } = {}) {
  return render(<MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>);
}

describe('MobileHeader', () => {
  it('renders nav title when provided', () => {
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
      />,
    );
    expect(screen.getByText('Songs')).toBeDefined();
  });

  it('renders back link when backFallback provided and no navTitle', () => {
    const { container } = renderWithRouter(
      <MobileHeader
        navTitle={null}
        backFallback="/songs"
        shouldAnimate={false}
        locationKey="/songs/abc"
        songInstrument={null}
        isSongsRoute={false}
      />,
    );
    const link = container.querySelector('a');
    expect(link).toBeTruthy();
  });

  it('returns null when no navTitle and no backFallback', () => {
    const { container } = renderWithRouter(
      <MobileHeader
        navTitle={null}
        backFallback={null}
        shouldAnimate={false}
        locationKey="/"
        songInstrument={null}
        isSongsRoute={false}
      />,
    );
    expect(container.innerHTML).toBe('');
  });
});
