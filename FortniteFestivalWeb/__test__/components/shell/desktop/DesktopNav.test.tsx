import { describe, it, expect, vi, beforeEach } from 'vitest';
import { render, screen, fireEvent, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import DesktopNav from '../../../../src/components/shell/desktop/DesktopNav';

const mockNavigate = vi.fn();
vi.mock('react-router-dom', async (importOriginal) => {
  const actual = await importOriginal() as Record<string, unknown>;
  return { ...actual, useNavigate: () => mockNavigate };
});

beforeEach(() => { vi.clearAllMocks(); });

function renderWithRouter(ui: React.ReactElement) {
  return render(<MemoryRouter>{ui}</MemoryRouter>);
}

describe('DesktopNav', () => {
  it('renders a nav element', () => {
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={() => {}} onProfileClick={() => {}} />);
    expect(screen.getByRole('navigation')).toBeDefined();
  });

  it('renders hamburger button', () => {
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={() => {}} onProfileClick={() => {}} />);
    expect(screen.getByLabelText('Open navigation')).toBeTruthy();
  });

  it('renders the mobile-style profile, search, and notifications actions', () => {
    renderWithRouter(<DesktopNav hasPlayer={true} onOpenSidebar={() => {}} onProfileClick={() => {}} onOpenSearch={() => {}} onOpenNotifications={() => {}} notificationCount={3} />);

    expect(screen.getByTestId('desktop-header-profile')).toBeTruthy();
    expect(screen.getByTestId('desktop-header-search')).toBeTruthy();
    expect(screen.getByTestId('desktop-header-notifications')).toBeTruthy();
    expect(screen.queryByText('Search')).toBeNull();
  });

  it('omits notifications when no notification opener is provided', () => {
    renderWithRouter(<DesktopNav hasPlayer={true} onOpenSidebar={() => {}} onProfileClick={() => {}} onOpenSearch={() => {}} />);

    expect(screen.queryByTestId('desktop-header-notifications')).toBeNull();
    expect(screen.queryByTestId('desktop-header-notifications-presence')).toBeNull();
  });

  it('calls onOpenSidebar when hamburger clicked', () => {
    const onOpen = vi.fn();
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={onOpen} onProfileClick={vi.fn()} />);
    const buttons = screen.getAllByRole('button');
    fireEvent.click(buttons[0]!);
    expect(onOpen).toHaveBeenCalled();
  });

  it('calls onProfileClick when profile button clicked', () => {
    const onProfile = vi.fn();
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={onProfile} />);
    fireEvent.click(screen.getByTestId('desktop-header-profile'));
    expect(onProfile).toHaveBeenCalled();
  });

  it('calls onOpenSearch when the search icon is clicked', () => {
    const onOpenSearch = vi.fn();
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} onOpenSearch={onOpenSearch} />);

    fireEvent.click(screen.getByTestId('desktop-header-search'));

    expect(onOpenSearch).toHaveBeenCalledTimes(1);
  });

  it('calls onOpenNotifications and renders badge state', () => {
    const onOpenNotifications = vi.fn();
    const { rerender } = renderWithRouter(
      <DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} onOpenNotifications={onOpenNotifications} notificationCount={12} />,
    );
    const notificationsButton = screen.getByTestId('desktop-header-notifications');

    expect(within(notificationsButton).getByText('9+')).toBeTruthy();
    fireEvent.click(notificationsButton);
    expect(onOpenNotifications).toHaveBeenCalledTimes(1);

    rerender(
      <MemoryRouter>
        <DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} onOpenNotifications={onOpenNotifications} notificationCount={0} />
      </MemoryRouter>,
    );

    expect(screen.getByTestId('desktop-header-notifications').getAttribute('data-notification-state')).toBe('empty');
    expect(within(screen.getByTestId('desktop-header-notifications')).queryByText('0')).toBeNull();

    rerender(
      <MemoryRouter>
        <DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} onOpenNotifications={onOpenNotifications} hasNotifications={true} notificationCount={0} />
      </MemoryRouter>,
    );

    expect(screen.getByTestId('desktop-header-notifications').getAttribute('data-notification-state')).toBe('populated');
    expect(screen.getByTestId('desktop-header-notifications').querySelector('svg path')?.getAttribute('fill')).toBeNull();
    expect(within(screen.getByTestId('desktop-header-notifications')).queryByText('0')).toBeNull();
  });

  it('renders notification swaps as an inert spinner in the existing desktop slot', () => {
    const onOpenNotifications = vi.fn();
    renderWithRouter(<DesktopNav hasPlayer={true} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} onOpenNotifications={onOpenNotifications} hasNotifications={true} notificationCount={7} notificationVisualState="spinner" />);

    const notificationsButton = screen.getByTestId('desktop-header-notifications');

    expect(screen.getByTestId('desktop-header-notifications-presence').getAttribute('data-visible')).toBe('true');
    expect(notificationsButton.getAttribute('data-notification-state')).toBe('loading');
    expect(notificationsButton.getAttribute('data-notification-visual-state')).toBe('spinner');
    expect(notificationsButton.getAttribute('aria-busy')).toBe('true');
    expect(notificationsButton.getAttribute('tabindex')).toBe('-1');
    expect(screen.getByTestId('desktop-header-notifications-spinner')).toBeTruthy();
    expect(within(notificationsButton).queryByText('7')).toBeNull();

    fireEvent.click(notificationsButton);

    expect(onOpenNotifications).not.toHaveBeenCalled();
  });

  it('orders desktop actions as profile, search, notifications', () => {
    renderWithRouter(<DesktopNav hasPlayer={true} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} onOpenSearch={vi.fn()} onOpenNotifications={vi.fn()} />);

    const profileButton = screen.getByTestId('desktop-header-profile');
    const searchButton = screen.getByTestId('desktop-header-search');
    const notificationsButton = screen.getByTestId('desktop-header-notifications');

    expect(profileButton.compareDocumentPosition(searchButton) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(searchButton.compareDocumentPosition(notificationsButton) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});

describe('DesktopNav — isWideDesktop', () => {
  it('hides hamburger button when isWideDesktop is true', () => {
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} isWideDesktop />);
    expect(screen.queryByLabelText('Open navigation')).toBeNull();
  });

  it('keeps header actions available when isWideDesktop is true', () => {
    renderWithRouter(<DesktopNav hasPlayer={true} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} onOpenSearch={vi.fn()} onOpenNotifications={vi.fn()} isWideDesktop />);

    expect(screen.getByTestId('desktop-header-profile')).toBeTruthy();
    expect(screen.getByTestId('desktop-header-search')).toBeTruthy();
    expect(screen.getByTestId('desktop-header-notifications')).toBeTruthy();
  });

  it('still renders a nav element when isWideDesktop is true', () => {
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} isWideDesktop />);
    expect(screen.getByRole('navigation')).toBeDefined();
  });

  it('renders no hamburger when isWideDesktop', () => {
    renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} isWideDesktop />);
    expect(screen.queryByLabelText('Open navigation')).toBeNull();
  });

  it('renders sidebar spacer and inner container for content alignment', () => {
    const { container } = renderWithRouter(<DesktopNav hasPlayer={false} onOpenSidebar={vi.fn()} onProfileClick={vi.fn()} isWideDesktop />);
    const nav = container.querySelector('nav')!;
    // First and last children are the 240px spacers
    expect(nav.children).toHaveLength(3);
  });
});
