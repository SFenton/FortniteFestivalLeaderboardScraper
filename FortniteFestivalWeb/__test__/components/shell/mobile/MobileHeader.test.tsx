import { describe, it, expect, vi } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import MobileHeader from '../../../../src/components/shell/mobile/MobileHeader';

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
        onOpenSearch={() => {}}
      />,
    );
    expect(screen.getByText('Songs')).toBeDefined();
  });

  it('renders search action and calls onOpenSearch', () => {
    const onOpenSearch = vi.fn();
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        onOpenSearch={onOpenSearch}
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Search' }));

    expect(onOpenSearch).toHaveBeenCalledTimes(1);
  });

  it('renders SongsPage instrument icon before profile and search actions', () => {
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument="Solo_Guitar"
        isSongsRoute={true}
        profileType="none"
        profileLabel="Select Player Profile"
        onProfileAction={() => {}}
        onOpenSearch={() => {}}
      />,
    );

    const header = screen.getByText('Songs').parentElement;
    expect(header).toBeTruthy();
    const searchButton = within(header!).getByRole('button', { name: 'Search' });
    const profileButton = within(header!).getByRole('button', { name: 'Select Player Profile' });
    const instrumentIcon = within(header!).getByAltText('Solo_Guitar');

    expect(instrumentIcon.compareDocumentPosition(profileButton) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(profileButton.compareDocumentPosition(searchButton) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('renders unselected profile action and calls onProfileAction', () => {
    const onProfileAction = vi.fn();
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        profileType="none"
        profileLabel="Select Player Profile"
        onProfileAction={onProfileAction}
        onOpenSearch={() => {}}
      />,
    );

    const profileButton = screen.getByRole('button', { name: 'Select Player Profile' });
    expect(profileButton.getAttribute('data-profile-type')).toBe('none');
    expect(profileButton.style.width).toBe('40px');
    expect(profileButton.style.height).toBe('40px');
    expect(profileButton.style.background).toBe('none');

    fireEvent.click(profileButton);

    expect(onProfileAction).toHaveBeenCalledTimes(1);
  });

  it('renders search as a larger mobile tap target', () => {
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        onOpenSearch={() => {}}
      />,
    );

    const searchButton = screen.getByRole('button', { name: 'Search' });
    expect(searchButton.style.width).toBe('40px');
    expect(searchButton.style.height).toBe('40px');
  });

  it('renders selected player profile action', () => {
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        profileType="player"
        profileLabel="View SFentonX's Profile"
        onProfileAction={() => {}}
        onOpenSearch={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: "View SFentonX's Profile" }).getAttribute('data-profile-type')).toBe('player');
  });

  it('renders selected band profile action', () => {
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        profileType="band"
        profileLabel="View band SFentonX + Phankie.ToT"
        onProfileAction={() => {}}
        onOpenSearch={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: 'View band SFentonX + Phankie.ToT' }).getAttribute('data-profile-type')).toBe('band');
  });

  it('does not render song instrument icon outside SongsPage', () => {
    renderWithRouter(
      <MobileHeader
        navTitle="Rankings"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/leaderboards"
        songInstrument="Solo_Guitar"
        isSongsRoute={false}
        onOpenSearch={() => {}}
      />,
    );

    expect(screen.queryByAltText('Solo_Guitar')).toBeNull();
    expect(screen.getByRole('button', { name: 'Search' })).toBeDefined();
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
        profileType="none"
        profileLabel="Select Player Profile"
        onProfileAction={() => {}}
        onOpenSearch={() => {}}
      />,
    );
    const link = container.querySelector('a');
    expect(link).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Select Player Profile' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Search' })).toBeDefined();
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
        onOpenSearch={() => {}}
      />,
    );
    expect(container.innerHTML).toBe('');
  });
});
