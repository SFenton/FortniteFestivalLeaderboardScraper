import { describe, it, expect, vi, beforeAll } from 'vitest';
import { fireEvent, render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import MobileHeader from '../../../../src/components/shell/mobile/MobileHeader';
import { stubElementDimensions, stubResizeObserver } from '../../../helpers/browserStubs';

function renderWithRouter(ui: React.ReactElement, { route = '/songs' } = {}) {
  return render(<MemoryRouter initialEntries={[route]}>{ui}</MemoryRouter>);
}

describe('MobileHeader', () => {
  beforeAll(() => {
    stubElementDimensions();
    stubResizeObserver();
    const originalCreateRange = document.createRange.bind(document);
    document.createRange = () => {
      const range = originalCreateRange();
      Object.assign(range, {
        getBoundingClientRect: () => ({
          top: 0,
          left: 0,
          bottom: 16,
          right: 120,
          width: 120,
          height: 16,
          x: 0,
          y: 0,
          toJSON() { return this; },
        }),
        getClientRects: () => [] as unknown as DOMRectList,
      });
      return range;
    };
  });

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
    const titleContainer = screen.getByText('Songs').parentElement;
    expect(titleContainer?.style.flex).toContain('1');
    expect(titleContainer?.style.minWidth).toBe('0px');
    expect(titleContainer?.style.overflow).toBe('hidden');
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

  it('renders notifications action to the right of search and calls onOpenNotifications', () => {
    const onOpenNotifications = vi.fn();
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        onOpenSearch={() => {}}
        onOpenNotifications={onOpenNotifications}
        notificationCount={5}
      />,
    );

    const searchButton = screen.getByRole('button', { name: 'Search' });
    const notificationsButton = screen.getByRole('button', { name: 'Notifications' });
    expect(searchButton.compareDocumentPosition(notificationsButton) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(within(notificationsButton).getByText('5')).toBeTruthy();

    fireEvent.click(notificationsButton);

    expect(onOpenNotifications).toHaveBeenCalledTimes(1);
  });

  it('keeps notification space mounted with an inert spinner during profile swaps', () => {
    const onOpenNotifications = vi.fn();
    renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        onOpenSearch={() => {}}
        onOpenNotifications={onOpenNotifications}
        hasNotifications={true}
        notificationCount={3}
        notificationVisualState="spinnerIn"
      />,
    );

    const notificationsButton = screen.getByRole('button', { name: 'Notifications' });

    expect(screen.getByTestId('mobile-header-notifications-presence').getAttribute('data-visible')).toBe('true');
    expect(notificationsButton.getAttribute('data-notification-state')).toBe('loading');
    expect(notificationsButton.getAttribute('data-notification-visual-state')).toBe('spinnerIn');
    expect(notificationsButton.getAttribute('aria-busy')).toBe('true');
    expect(notificationsButton.getAttribute('aria-disabled')).toBe('true');
    expect(notificationsButton.getAttribute('tabindex')).toBe('-1');
    expect(screen.getByTestId('mobile-header-notifications-spinner').style.transform).toBe('translateY(4px)');
    expect(within(notificationsButton).queryByText('3')).toBeNull();

    fireEvent.click(notificationsButton);

    expect(onOpenNotifications).not.toHaveBeenCalled();
  });

  it('omits the notifications action when notifications are unavailable', () => {
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

    expect(screen.queryByRole('button', { name: 'Notifications' })).toBeNull();
    expect(screen.queryByTestId('mobile-header-notifications-presence')).toBeNull();
  });

  it('keeps the notifications action mounted but inert while fading out', () => {
    const { rerender } = renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        onOpenSearch={() => {}}
        onOpenNotifications={() => {}}
        hasNotifications={true}
        notificationCount={12}
      />,
    );

    expect(screen.getByTestId('mobile-header-notifications-presence').getAttribute('data-visible')).toBe('true');
    expect(screen.getByTestId('mobile-header-notifications').getAttribute('data-notification-state')).toBe('populated');
    expect(within(screen.getByTestId('mobile-header-notifications')).getByText('9+')).toBeTruthy();

    rerender(
      <MemoryRouter initialEntries={['/songs']}>
        <MobileHeader
          navTitle="Songs"
          backFallback={null}
          shouldAnimate={false}
          locationKey="/songs"
          songInstrument={null}
          isSongsRoute={true}
          onOpenSearch={() => {}}
        />
      </MemoryRouter>,
    );

    expect(screen.getByTestId('mobile-header-notifications-presence').getAttribute('data-visible')).toBe('false');
    expect(screen.getByTestId('mobile-header-notifications').getAttribute('tabindex')).toBe('-1');
    expect(screen.getByTestId('mobile-header-notifications').getAttribute('data-notification-state')).toBe('populated');
    expect(screen.getByTestId('mobile-header-notifications').querySelector('svg path')?.getAttribute('fill')).toBeNull();
    expect(within(screen.getByTestId('mobile-header-notifications')).getByText('9+')).toBeTruthy();
  });

  it('uses the empty bell state at zero, full bell when the feed has notifications, and caps counts at 9+', () => {
    const { rerender } = renderWithRouter(
      <MobileHeader
        navTitle="Songs"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/songs"
        songInstrument={null}
        isSongsRoute={true}
        onOpenSearch={() => {}}
        onOpenNotifications={() => {}}
        notificationCount={0}
      />,
    );

    const notificationsButton = screen.getByRole('button', { name: 'Notifications' });
    expect(notificationsButton.getAttribute('data-notification-state')).toBe('empty');
    expect(within(notificationsButton).queryByText('0')).toBeNull();

    rerender(
      <MemoryRouter initialEntries={['/songs']}>
        <MobileHeader
          navTitle="Songs"
          backFallback={null}
          shouldAnimate={false}
          locationKey="/songs"
          songInstrument={null}
          isSongsRoute={true}
          onOpenSearch={() => {}}
          onOpenNotifications={() => {}}
          hasNotifications={true}
          notificationCount={0}
        />
      </MemoryRouter>,
    );

    expect(screen.getByRole('button', { name: 'Notifications' }).getAttribute('data-notification-state')).toBe('populated');
    expect(within(screen.getByRole('button', { name: 'Notifications' })).queryByText('0')).toBeNull();
    expect(screen.getByRole('button', { name: 'Notifications' }).querySelector('svg path')?.getAttribute('fill')).toBeNull();

    rerender(
      <MemoryRouter initialEntries={['/songs']}>
        <MobileHeader
          navTitle="Songs"
          backFallback={null}
          shouldAnimate={false}
          locationKey="/songs"
          songInstrument={null}
          isSongsRoute={true}
          onOpenSearch={() => {}}
          onOpenNotifications={() => {}}
          hasNotifications={true}
          notificationCount={12}
        />
      </MemoryRouter>,
    );

    const populatedNotificationsButton = screen.getByRole('button', { name: 'Notifications' });
    expect(populatedNotificationsButton.getAttribute('data-notification-state')).toBe('populated');
    expect(populatedNotificationsButton.querySelector('svg path')?.getAttribute('fill')).toBeNull();
    expect(within(populatedNotificationsButton).getByText('9+')).toBeTruthy();
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

    const header = screen.getByTestId('mobile-header-actions').parentElement;
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

  it('renders mobile header actions as bright white', () => {
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
        onOpenSidebar={() => {}}
        onProfileAction={() => {}}
        onOpenSearch={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: 'Open navigation' }).style.color).toBe('rgb(255, 255, 255)');
    expect(screen.getByRole('button', { name: 'Search' }).style.color).toBe('rgb(255, 255, 255)');
    expect(screen.getByRole('button', { name: 'Select Player Profile' }).style.color).toBe('rgb(255, 255, 255)');
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

  it('renders selected band statistics as a root header, not a back header', () => {
    renderWithRouter(
      <MobileHeader
        navTitle="Statistics"
        backFallback={null}
        shouldAnimate={false}
        locationKey="/statistics"
        songInstrument={null}
        isSongsRoute={false}
        profileType="band"
        profileLabel="View band SFentonX + Phankie.ToT"
        onOpenSidebar={() => {}}
        onProfileAction={() => {}}
        onOpenSearch={() => {}}
      />,
    );

    expect(screen.getByRole('button', { name: 'Open navigation' })).toBeDefined();
    expect(screen.getByText('Statistics')).toBeDefined();
    expect(screen.queryByRole('link', { name: /Back/i })).toBeNull();
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
