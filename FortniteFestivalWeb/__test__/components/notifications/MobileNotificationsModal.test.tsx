import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen } from '@testing-library/react';
import MobileNotificationsModal, { mockMobileNotifications, sortNotificationsNewestFirst, type MobileNotification } from '../../../src/components/notifications/MobileNotificationsModal';

const mockIsMobile = vi.fn(() => true);
const BRIGHT_WHITE = 'rgb(255, 255, 255)';
vi.mock('../../../src/hooks/ui/useIsMobile', () => ({ useIsMobile: () => mockIsMobile() }));

vi.mock('../../../src/hooks/ui/useVisualViewport', () => ({
  useVisualViewportHeight: () => 844,
  useVisualViewportOffsetTop: () => 0,
}));

beforeEach(() => {
  mockIsMobile.mockReturnValue(true);
  vi.spyOn(window, 'requestAnimationFrame').mockImplementation((cb) => { cb(0); return 0; });
  vi.spyOn(window, 'cancelAnimationFrame').mockImplementation(() => {});
});


afterEach(() => {
  vi.restoreAllMocks();
});

function domRect(top: number, bottom: number): DOMRect {
  const height = bottom - top;
  return {
    x: 0,
    y: top,
    top,
    bottom,
    left: 0,
    right: 320,
    width: 320,
    height,
    toJSON: () => ({}),
  } as DOMRect;
}

function setElementRect(element: Element, top: number, bottom: number) {
  vi.spyOn(element, 'getBoundingClientRect').mockReturnValue(domRect(top, bottom));
}

function flushAnimationFrames(callbacks: FrameRequestCallback[]) {
  const pendingCallbacks = callbacks.splice(0);
  act(() => {
    pendingCallbacks.forEach(callback => callback(0));
  });
}

describe('MobileNotificationsModal', () => {
  it('renders hardcoded coalesced notification rows', () => {
    render(<MobileNotificationsModal visible={true} onClose={() => {}} />);

    expect(screen.getByRole('dialog', { name: 'Notifications' })).toBeTruthy();
    expect(screen.queryByText('Latest')).toBeNull();
    const list = screen.getByTestId('notification-list');
    expect(list.style.overflowY).toBe('auto');
    expect(list.getAttribute('data-scroll-fade')).toBe('true');
    expect(Number(list.getAttribute('data-scroll-fade-top-padding'))).toBeGreaterThanOrEqual(18);
    expect(list.getAttribute('data-safe-area-bottom')).toBe('true');
    const rows = screen.getAllByTestId('mock-notification-row');
    expect(rows).toHaveLength(mockMobileNotifications.length);
    const detectedTimes = rows.map(row => Date.parse(row.getAttribute('data-detected-at') ?? ''));
    expect(detectedTimes).toEqual([...detectedTimes].sort((left, right) => right - left));
    rows.forEach((row) => {
      expect(row.style.alignItems).toBe('center');
    });
    const mediaRails = screen.getAllByTestId('notification-media-rail');
    expect(mediaRails).toHaveLength(mockMobileNotifications.length);
    expect(new Set(mediaRails.map((rail) => rail.getAttribute('data-media-size')))).toEqual(new Set(['64']));
    mediaRails.forEach((rail) => {
      expect(rail.style.background).toBe('');
      expect(rail.style.border).toBe('');
    });
    expect(screen.getByAltText('Apple album art')).toBeTruthy();
    expect(screen.getByAltText('Stand and Fight (Remix) album art')).toBeTruthy();
    expect(screen.getByAltText("Ghosts 'n' Stuff album art")).toBeTruthy();
    screen.getAllByTestId('notification-title').forEach((title) => {
      expect(title.style.color).toBe(BRIGHT_WHITE);
    });
    screen.getAllByTestId('notification-summary').forEach((summary) => {
      expect(summary.style.color).toBe(BRIGHT_WHITE);
    });
    screen.getAllByTestId('notification-flag').forEach((flag) => {
      expect(flag.style.color).toBe(BRIGHT_WHITE);
    });
    expect(document.body.querySelector('[data-media-kind="soloInstrument"] img[alt="Solo_Drums"]')).toBeTruthy();
    expect(document.body.querySelector('[data-media-layout="duoStack"]')).toBeTruthy();
    expect(document.body.querySelectorAll('[data-media-layout="duoStack"] img')).toHaveLength(2);
    expect(document.body.querySelector('[data-media-layout="comboGrid"]')).toBeTruthy();
    expect(document.body.querySelectorAll('[data-media-layout="comboGrid"] img')).toHaveLength(3);
    expect(screen.getAllByText('Apple').length).toBeGreaterThan(0);
    expect(screen.queryByText('SFentonX - Pro Drums')).toBeNull();
    expect(screen.queryByText('Today 7:53 AM')).toBeNull();
    const modalText = document.body.textContent ?? '';
    expect(modalText).toContain('You set a new personal best on Apple with 137,700, earned gold stars, and climbed from #1,214 to #982.');
    expect(modalText).toContain("Your first play on Ghosts 'n' Stuff scored 180,005, started at #1,288, and earned gold stars.");
    expect(modalText).toContain('Your band set a new best score on Apple with 1,234,567, got a Full Combo, earned gold stars, climbed from #42 to #31 in Band Trios, and climbed from #9 to #6 for Bass/Bass/Drums.');
    const emphasizedText = Array.from(document.body.querySelectorAll('[data-notification-emphasis="true"]')).map((element) => element.textContent);
    expect(emphasizedText).toContain('180,005');
    expect(emphasizedText).toContain('#1,288');
    expect(emphasizedText).toContain('gold stars');
    expect(emphasizedText).toContain('Bass/Bass/Drums');
    expect(emphasizedText).not.toContain('Band Duos weighted rank');
    expect(screen.getAllByText('New High Score').length).toBeGreaterThan(0);
    expect(screen.getAllByText('First Play').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Gold Stars').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Rank Up').length).toBeGreaterThan(0);
  });

  it('sorts notification data newest to oldest without mutating the source array', () => {
    const notifications = [
      { eventId: 1, detectedAt: '2026-05-09T14:00:00Z' },
      { eventId: 2, detectedAt: '2026-05-09T15:00:00Z' },
      { eventId: 3, detectedAt: 'bad-date' },
    ] as MobileNotification[];

    const sortedNotifications = sortNotificationsNewestFirst(notifications);

    expect(sortedNotifications.map(notification => notification.eventId)).toEqual([2, 1, 3]);
    expect(notifications.map(notification => notification.eventId)).toEqual([1, 2, 3]);
  });

  it('keeps unread dots stable for the current modal session', () => {
    const unreadNotificationIds = new Set([mockMobileNotifications[0]!.notificationGuid]);
    const { rerender } = render(
      <MobileNotificationsModal visible={true} onClose={() => {}} unreadNotificationIds={unreadNotificationIds} />,
    );

    expect(screen.getAllByTestId('notification-unread-dot')).toHaveLength(1);
    expect(screen.getAllByTestId('mock-notification-row')[0]!.getAttribute('data-unread')).toBe('true');

    rerender(<MobileNotificationsModal visible={true} onClose={() => {}} unreadNotificationIds={new Set()} />);

    expect(screen.getAllByTestId('notification-unread-dot')).toHaveLength(1);

    rerender(<MobileNotificationsModal visible={false} onClose={() => {}} unreadNotificationIds={new Set()} />);
    rerender(<MobileNotificationsModal visible={true} onClose={() => {}} unreadNotificationIds={new Set()} />);

    expect(screen.queryByTestId('notification-unread-dot')).toBeNull();
  });

  it('marks unread notifications seen when a row is at least 90 percent visible', () => {
    const frameCallbacks: FrameRequestCallback[] = [];
    vi.mocked(window.requestAnimationFrame).mockImplementation((callback) => {
      frameCallbacks.push(callback);
      return frameCallbacks.length;
    });
    vi.mocked(window.cancelAnimationFrame).mockImplementation(() => {});

    const onNotificationsSeen = vi.fn();
    const notifications = mockMobileNotifications.slice(0, 2);
    render(
      <MobileNotificationsModal
        visible={true}
        onClose={() => {}}
        notifications={notifications}
        unreadNotificationIds={new Set(notifications.map(notification => notification.notificationGuid))}
        onNotificationsSeen={onNotificationsSeen}
      />,
    );

    const list = screen.getByTestId('notification-list');
    const rows = screen.getAllByTestId('mock-notification-row');
    setElementRect(list, 0, 100);
    setElementRect(rows[0]!, 0, 100);
    setElementRect(rows[1]!, 11, 111);

    flushAnimationFrames(frameCallbacks);
    fireEvent.transitionEnd(screen.getByRole('dialog', { name: 'Notifications' }));
    flushAnimationFrames(frameCallbacks);

    expect(onNotificationsSeen).toHaveBeenCalledTimes(1);
    expect(onNotificationsSeen).toHaveBeenLastCalledWith([notifications[0]!.notificationGuid]);

    setElementRect(rows[1]!, 10, 110);
    fireEvent.scroll(list);
    flushAnimationFrames(frameCallbacks);

    expect(onNotificationsSeen).toHaveBeenCalledTimes(2);
    expect(onNotificationsSeen).toHaveBeenLastCalledWith([notifications[1]!.notificationGuid]);
  });
});