import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, fireEvent, render, screen, within } from '@testing-library/react';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import MobileNotificationsModal, {
  filterSurfaceNotifications,
  mockEmptyMobileNotifications,
  mockMobileNotifications,
  notificationSurfaceInstrument,
  shouldSurfaceNotification,
  sortNotificationsNewestFirst,
  type MobileNotification,
} from '../../../src/components/notifications/MobileNotificationsModal';

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
  it('filters surfaced notifications by active solo instruments while keeping band rows', () => {
    const visibleInstruments = new Set<ServerInstrumentKey>(['Solo_Guitar', 'Solo_Bass']);

    const surfacedNotifications = filterSurfaceNotifications(mockMobileNotifications, visibleInstruments);

    expect(surfacedNotifications.map(notification => notification.eventId)).toEqual([5, 6]);
    expect(surfacedNotifications.every(notification => !notification.eventKind.startsWith('player_'))).toBe(true);
  });

  it('does not filter notifications when no instrument filter is active', () => {
    expect(filterSurfaceNotifications(mockMobileNotifications, null).map(notification => notification.eventId)).toEqual(
      mockMobileNotifications.map(notification => notification.eventId),
    );
    expect(filterSurfaceNotifications(mockMobileNotifications, undefined).map(notification => notification.eventId)).toEqual(
      mockMobileNotifications.map(notification => notification.eventId),
    );
  });

  it('detects rank notification instruments from solo media fallbacks', () => {
    const rankNotification = mockMobileNotifications.find(notification => notification.eventKind === 'player_weighted_rank_improved')!;

    expect(rankNotification.instrument).toBeUndefined();
    expect(notificationSurfaceInstrument(rankNotification)).toBe('Solo_Drums');
    expect(shouldSurfaceNotification(rankNotification, new Set<ServerInstrumentKey>(['Solo_Guitar']))).toBe(false);
    expect(shouldSurfaceNotification(rankNotification, new Set<ServerInstrumentKey>(['Solo_Drums']))).toBe(true);
  });

  it('filters aggregate rank notifications by the experimental ranks setting', () => {
    const rankNotification: MobileNotification = {
      ...mockMobileNotifications[3]!,
      eventKind: 'player_total_score_rank_improved',
      metric: 'total_score_rank',
      oldRank: 263,
      newRank: 189,
      payload: {
        coalescedEventCount: 2,
        coalescedEventKinds: ['player_total_score_rank_improved', 'player_weighted_rank_improved'],
        coalescedEvents: [
          { eventKind: 'player_total_score_rank_improved', metric: 'total_score_rank', oldRank: 263, newRank: 189 },
          { eventKind: 'player_weighted_rank_improved', metric: 'weighted_rank', oldRank: 201, newRank: 163 },
        ],
      },
    };

    const hiddenExperimental: MobileNotification = {
      ...rankNotification,
      eventKind: 'player_weighted_rank_improved',
      metric: 'weighted_rank',
      payload: {
        coalescedEventCount: 1,
        coalescedEventKinds: ['player_weighted_rank_improved'],
        coalescedEvents: [
          { eventKind: 'player_weighted_rank_improved', metric: 'weighted_rank', oldRank: 201, newRank: 163 },
        ],
      },
    };

    const visibleWithToggleOff = filterSurfaceNotifications([rankNotification], {
      visibleInstruments: new Set<ServerInstrumentKey>(['Solo_Drums']),
      enableExperimentalRanks: false,
    });
    const hiddenWithToggleOff = filterSurfaceNotifications([hiddenExperimental], {
      visibleInstruments: new Set<ServerInstrumentKey>(['Solo_Drums']),
      enableExperimentalRanks: false,
    });
    const visibleWithToggleOn = filterSurfaceNotifications([rankNotification], {
      visibleInstruments: new Set<ServerInstrumentKey>(['Solo_Drums']),
      enableExperimentalRanks: true,
    });

    expect(visibleWithToggleOff).toHaveLength(1);
    expect(visibleWithToggleOff[0]!.eventKind).toBe('player_total_score_rank_improved');
    expect(visibleWithToggleOff[0]!.navigation?.rankBy).toBe('totalscore');
    expect(visibleWithToggleOff[0]!.payload.coalescedEvents).toHaveLength(1);
    expect(hiddenWithToggleOff).toHaveLength(0);
    expect(visibleWithToggleOn[0]!.payload.coalescedEvents).toHaveLength(2);
  });

  it('keeps aggregate progress facts when paired experimental rank children are filtered out', () => {
    const notification: MobileNotification = {
      ...mockMobileNotifications[3]!,
      eventKind: 'player_fc_count_improved',
      metric: 'full_combo_count',
      oldNumeric: 649,
      newNumeric: 655,
      payload: {
        coalescedEventCount: 2,
        coalescedEventKinds: ['player_fc_count_improved', 'player_fc_rate_rank_improved'],
        coalescedEvents: [
          { eventKind: 'player_fc_count_improved', metric: 'full_combo_count', oldNumeric: 649, newNumeric: 655 },
          { eventKind: 'player_fc_rate_rank_improved', metric: 'fc_rate_rank', oldRank: 4, newRank: 1 },
        ],
      },
    };

    const visibleWithToggleOff = filterSurfaceNotifications([notification], {
      visibleInstruments: new Set<ServerInstrumentKey>(['Solo_Drums']),
      enableExperimentalRanks: false,
    });

    expect(visibleWithToggleOff).toHaveLength(1);
    expect(visibleWithToggleOff[0]!.eventKind).toBe('player_fc_count_improved');
    expect(visibleWithToggleOff[0]!.payload.coalescedEventKinds).toEqual(['player_fc_count_improved']);
    expect(visibleWithToggleOff[0]!.payload.coalescedEvents).toEqual([
      { eventKind: 'player_fc_count_improved', metric: 'full_combo_count', oldNumeric: 649, newNumeric: 655 },
    ]);
  });

  it('renders album art with a two-column affected instrument grid for multi-instrument song notifications', () => {
    const notification: MobileNotification = {
      ...mockMobileNotifications[0]!,
      notificationGuid: 'multi-instrument-song-notification',
      instrument: 'Solo_Guitar',
      instrumentLabel: 'Lead',
      media: {
        kind: 'songInstrumentGrid',
        albumArt: 'https://cdn2.unrealengine.com/taxes.jpg',
        alt: 'Taxes album art',
        instruments: ['Solo_Guitar', 'Solo_Drums', 'Solo_Vocals'],
        label: 'Lead, Drums, Tap Vocals',
      },
      surfaceInstruments: ['Solo_Guitar', 'Solo_Drums', 'Solo_Vocals'],
      payload: {
        coalescedEventCount: 3,
        coalescedEventKinds: ['player_score_pb', 'player_fc_achieved', 'player_gold_stars_achieved'],
        coalescedInstruments: ['Solo_Guitar', 'Solo_Drums', 'Solo_Vocals'],
        coalescedEvents: [
          { eventKind: 'player_score_pb', instrument: 'Solo_Guitar', instrumentLabel: 'Lead', metric: 'score', oldNumeric: 1, newNumeric: 2 },
          { eventKind: 'player_fc_achieved', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'full_combo' },
          { eventKind: 'player_gold_stars_achieved', instrument: 'Solo_Vocals', instrumentLabel: 'Tap Vocals', metric: 'stars' },
        ],
      },
    };

    render(<MobileNotificationsModal visible={true} onClose={() => {}} notifications={[notification]} />);

    const rail = screen.getByTestId('notification-media-rail');
    const grid = screen.getByTestId('notification-media-song-instrument-grid');
    expect(rail.getAttribute('data-media-kind')).toBe('songInstrumentGrid');
    expect(rail.getAttribute('aria-label')).toBe('Affected instruments: Lead, Drums, Tap Vocals');
    expect(screen.getByAltText('Taxes album art')).toBeTruthy();
    expect(grid.style.gridTemplateColumns).toBe('18px 18px');
    expect(grid.querySelectorAll('img')).toHaveLength(3);
    expect(screen.queryByTestId('notification-flags')).toBeNull();
    const flagGroups = screen.getAllByTestId('notification-flag-group');
    expect(flagGroups).toHaveLength(3);
    expect(flagGroups.map(group => group.getAttribute('data-instrument'))).toEqual(['Solo_Guitar', 'Solo_Drums', 'Solo_Vocals']);
    expect(flagGroups.map(group => group.getAttribute('aria-label'))).toEqual([
      'Lead: New High Score',
      'Drums: Full Combo',
      'Tap Vocals: Gold Stars',
    ]);
    expect(within(flagGroups[0]!).getByAltText('Solo_Guitar')).toBeTruthy();
    expect(within(flagGroups[0]!).getByText('New High Score')).toBeTruthy();
    expect(within(flagGroups[1]!).getByAltText('Solo_Drums')).toBeTruthy();
    expect(within(flagGroups[1]!).getByText('Full Combo')).toBeTruthy();
    expect(within(flagGroups[2]!).getByAltText('Solo_Vocals')).toBeTruthy();
    expect(within(flagGroups[2]!).getByText('Gold Stars')).toBeTruthy();
  });

  it('renders grouped notification statements with visible line breaks', () => {
    const notification: MobileNotification = {
      ...mockMobileNotifications[3]!,
      notificationGuid: 'instrument-aggregate-notification',
      eventKind: 'player_total_score_improved',
      metric: 'total_score',
      oldNumeric: 91257683,
      newNumeric: 91743538,
      instrument: 'Solo_Vocals',
      instrumentLabel: 'Tap Vocals',
      media: { kind: 'soloInstrument', instrument: 'Solo_Vocals', label: 'Tap Vocals' },
      surfaceInstruments: ['Solo_Vocals'],
      payload: {
        coalescedEventCount: 3,
        coalescedEventKinds: ['player_total_score_improved', 'player_total_score_rank_improved', 'player_fc_count_improved'],
        coalescedEvents: [
          { eventKind: 'player_total_score_improved', metric: 'total_score', oldNumeric: 91257683, newNumeric: 91743538 },
          { eventKind: 'player_total_score_rank_improved', metric: 'total_score_rank', oldRank: 12, newRank: 10 },
          { eventKind: 'player_fc_count_improved', metric: 'full_combo_count', oldNumeric: 649, newNumeric: 655 },
        ],
      },
    };

    render(<MobileNotificationsModal visible={true} onClose={() => {}} notifications={[notification]} />);

    expect(screen.getByText('Tap Vocals · Improvements')).toBeTruthy();
    const summary = screen.getByTestId('notification-summary');
    expect(summary.style.whiteSpace).toBe('pre-line');
    expect(summary.textContent).toBe('Your total score increased to 91,743,538 points and your total score rank moved up from #12 to #10.\n\nYour Full Combo count increased to 655.');
  });

  it('renders Full Combo and Gold Stars pills when a PB score already has those statuses', () => {
    const notification: MobileNotification = {
      ...mockMobileNotifications[0]!,
      notificationGuid: 'pb-result-status-notification',
      payload: {
        coalescedEventCount: 1,
        coalescedEventKinds: ['player_score_pb'],
        coalescedEvents: [
          { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 127025, newNumeric: 137700 },
        ],
        oldFullCombo: true,
        newFullCombo: true,
        oldStars: 6,
        newStars: 6,
      },
    };

    render(<MobileNotificationsModal visible={true} onClose={() => {}} notifications={[notification]} />);

    expect(screen.getByTestId('notification-summary').textContent).toBe('You set a new personal best on Pro Drums for Apple with 137,700 points, got a Full Combo, and earned gold stars.');
    const flags = screen.getByTestId('notification-flags');
    expect(within(flags).getByText('New High Score')).toBeTruthy();
    expect(within(flags).getByText('Full Combo')).toBeTruthy();
    expect(within(flags).getByText('Gold Stars')).toBeTruthy();
  });

  it('renders primary-child Full Combo and Gold Stars pills in multi-instrument PB notifications', () => {
    const notification: MobileNotification = {
      ...mockMobileNotifications[0]!,
      notificationGuid: 'multi-primary-pb-result-status-notification',
      title: 'Inferno Island',
      songTitle: 'Inferno Island',
      eventKind: 'player_score_pb',
      instrument: 'Solo_Drums',
      instrumentLabel: 'Drums',
      metric: 'score',
      oldNumeric: 203946,
      newNumeric: 214668,
      media: {
        kind: 'songInstrumentGrid',
        albumArt: 'https://cdn2.unrealengine.com/inferno-island.jpg',
        alt: 'Inferno Island album art',
        instruments: ['Solo_Drums', 'Solo_PeripheralCymbals'],
        label: 'Drums, Pro Drums + Cymbals',
      },
      surfaceInstruments: ['Solo_Drums', 'Solo_PeripheralCymbals'],
      payload: {
        coalescedEventCount: 3,
        coalescedEventKinds: ['player_score_pb', 'player_song_rank_improved', 'player_first_score'],
        coalescedInstruments: ['Solo_Drums', 'Solo_PeripheralCymbals'],
        coalescedEvents: [
          { eventKind: 'player_score_pb', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'score', oldNumeric: 203946, newNumeric: 214668 },
          { eventKind: 'player_song_rank_improved', instrument: 'Solo_Drums', instrumentLabel: 'Drums', metric: 'song_rank', oldRank: 342, newRank: 56 },
          { eventKind: 'player_first_score', instrument: 'Solo_PeripheralCymbals', instrumentLabel: 'Pro Drums + Cymbals', metric: 'score', newNumeric: 201919, newRank: 6 },
        ],
        oldFullCombo: true,
        newFullCombo: true,
        oldStars: 6,
        newStars: 6,
      },
    };

    render(<MobileNotificationsModal visible={true} onClose={() => {}} notifications={[notification]} />);

    expect(screen.getByTestId('notification-summary').textContent).toBe('For Drums, your play set a new personal best with 214,668 points, got a Full Combo, earned gold stars, and climbed from #342 to #56.\n\nFor Pro Drums + Cymbals, your first play scored 201,919 points and started at #6.');
    expect(screen.queryByTestId('notification-flags')).toBeNull();
    const flagGroups = screen.getAllByTestId('notification-flag-group');
    expect(flagGroups).toHaveLength(2);
    expect(flagGroups.map(group => group.getAttribute('data-instrument'))).toEqual(['Solo_Drums', 'Solo_PeripheralCymbals']);
    expect(within(flagGroups[0]!).getByText('New High Score')).toBeTruthy();
    expect(within(flagGroups[0]!).getByText('Full Combo')).toBeTruthy();
    expect(within(flagGroups[0]!).getByText('Gold Stars')).toBeTruthy();
    expect(within(flagGroups[0]!).getByText('Rank Up')).toBeTruthy();
    expect(within(flagGroups[1]!).getByText('First Play')).toBeTruthy();
    expect(within(flagGroups[1]!).queryByText('Full Combo')).toBeNull();
    expect(within(flagGroups[1]!).queryByText('Gold Stars')).toBeNull();
  });

  it('renders a compact empty state without notification sections', () => {
    render(<MobileNotificationsModal visible={true} onClose={() => {}} notifications={mockEmptyMobileNotifications} />);

    expect(screen.getByRole('dialog', { name: 'Notifications' })).toBeTruthy();
    expect(screen.getByTestId('notification-empty-state')).toBeTruthy();
    expect(screen.getByTestId('notification-empty-icon')).toBeTruthy();
    expect(screen.getByText('No notifications available')).toBeTruthy();
    expect(screen.getByText('Notifications will appear here when new high scores are set or global ranks improve. Set new high scores and compete with friends to see them!')).toBeTruthy();
    expect(screen.queryByTestId('mock-notification-row')).toBeNull();
    expect(screen.queryByTestId('notification-section')).toBeNull();
    expect(screen.queryByTestId('notification-section-heading')).toBeNull();
  });

  it('renders the not-generated empty state copy when notifications have not been generated', () => {
    render(<MobileNotificationsModal visible={true} onClose={() => {}} notifications={mockEmptyMobileNotifications} notificationsGenerated={false} />);

    expect(screen.getByText('No notifications available')).toBeTruthy();
    expect(screen.getByText('Notifications may appear here after the next leaderboard update. Set new high scores and compete with friends to see them!')).toBeTruthy();
  });

  it('renders hardcoded coalesced notification rows', () => {
    render(<MobileNotificationsModal visible={true} onClose={() => {}} onNotificationOpen={() => {}} />);

    expect(screen.getByRole('dialog', { name: 'Notifications' })).toBeTruthy();
    expect(screen.queryByText('Latest')).toBeNull();
    expect(screen.getByTestId('notification-section-heading').textContent).toBe('Older');
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
      expect(row.getAttribute('data-actionable')).toBe('true');
    });
    expect(screen.getAllByTestId('notification-chevron')).toHaveLength(mockMobileNotifications.length);
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
    expect(screen.getByAltText('Apple band notification album art')).toBeTruthy();
    screen.getAllByTestId('notification-title').forEach((title) => {
      expect(title.style.color).toBe(BRIGHT_WHITE);
      expect(title.getAttribute('data-marquee-title')).toBe('true');
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
    expect(document.body.querySelectorAll('[data-testid="notification-media-combo-grid"] img')).toHaveLength(3);
    const cycle = document.body.querySelector('[data-testid="notification-media-cycle"]');
    expect(cycle).toBeTruthy();
    expect(cycle?.getAttribute('data-media-cycle')).toBe('freRowSwap');
    expect(cycle?.getAttribute('data-media-cycle-style')).toBe('rowReplace');
    expect(cycle?.getAttribute('data-media-cycle-epoch')).toBe('1700000000000');
    expect(cycle?.getAttribute('data-media-cycle-duration')).toBe('10');
    expect(cycle?.getAttribute('data-media-cycle-swap-interval')).toBe('5000');
    expect(cycle?.getAttribute('data-media-cycle-fade-ms')).toBe('400');
    expect(cycle?.getAttribute('data-media-cycle-active-layer')).toMatch(/^(icons|art)$/);
    expect(cycle?.getAttribute('data-media-cycle-fading')).toBe('false');
    const iconLayer = document.body.querySelector('[data-testid="notification-media-cycle-icons"]') as HTMLElement | null;
    const artLayer = document.body.querySelector('[data-testid="notification-media-cycle-art"]') as HTMLElement | null;
    expect(iconLayer).toBeTruthy();
    expect(artLayer).toBeTruthy();
    expect(iconLayer?.getAttribute('data-media-cycle-row-layer')).toBe('icons');
    expect(artLayer?.getAttribute('data-media-cycle-row-layer')).toBe('art');
    expect(iconLayer?.style.transition).toBe('opacity 400ms ease, transform 400ms ease');
    expect(artLayer?.style.transition).toBe('opacity 400ms ease, transform 400ms ease');
    expect(screen.getByText('Apple · Pro Drums')).toBeTruthy();
    expect(screen.getByText('Stand and Fight (Remix) · Drums')).toBeTruthy();
    expect(screen.getByText("Ghosts 'n' Stuff · Pro Drums")).toBeTruthy();
    expect(screen.getByText('Apple · Band Trios')).toBeTruthy();
    expect(screen.getAllByText('Weighted Percentile Rank Improved').length).toBe(2);
    expect(screen.queryByText('SFentonX - Pro Drums')).toBeNull();
    expect(screen.queryByText('Today 7:53 AM')).toBeNull();
    const modalText = document.body.textContent ?? '';
    expect(modalText).toContain('You set a new personal best on Pro Drums for Apple with 137,700 points, earned gold stars, and climbed from #1,214 to #982.');
    expect(modalText).toContain("Your first Pro Drums play on Ghosts 'n' Stuff scored 180,005 points, started at #1,288, and earned gold stars.");
    expect(modalText).toContain('Your band set a new best score on Apple with 1,234,567 points, got a Full Combo, earned gold stars, climbed from #42 to #31 in Band Trios, and climbed from #9 to #6 for Bass/Bass/Drums.');
    const emphasizedText = Array.from(document.body.querySelectorAll('[data-notification-emphasis="true"]')).map((element) => element.textContent);
    expect(emphasizedText).toContain('Pro Drums');
    expect(emphasizedText).toContain('180,005');
    expect(emphasizedText).toContain('#1,288');
    expect(emphasizedText).toContain('gold stars');
    expect(emphasizedText).toContain('Bass/Bass/Drums');
    expect(emphasizedText).not.toContain('Weighted Percentile Rank Improved');
    expect(screen.getAllByText('New High Score').length).toBeGreaterThan(0);
    expect(screen.getAllByText('First Play').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Gold Stars').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Rank Up').length).toBeGreaterThan(0);
  });

  it('partitions notifications into New and Older sections without changing unread status', () => {
    const sortedNotifications = sortNotificationsNewestFirst(mockMobileNotifications);
    const newNotification = sortedNotifications[1]!;
    const olderUnreadNotification = sortedNotifications[0]!;

    render(
      <MobileNotificationsModal
        visible={true}
        onClose={() => {}}
        notifications={sortedNotifications}
        newNotificationIds={new Set([newNotification.notificationGuid])}
        unreadNotificationIds={new Set([olderUnreadNotification.notificationGuid])}
        onNotificationOpen={() => {}}
      />,
    );

    const sections = screen.getAllByTestId('notification-section');
    expect(sections).toHaveLength(2);
    expect(sections[0]!.getAttribute('data-notification-section')).toBe('new');
    expect(within(sections[0]!).getByTestId('notification-section-heading').textContent).toBe('New');
    expect(sections[1]!.getAttribute('data-notification-section')).toBe('older');
    expect(within(sections[1]!).getByTestId('notification-section-heading').textContent).toBe('Older');

    const newRow = within(sections[0]!).getByTestId('mock-notification-row');
    expect(newRow.getAttribute('data-notification-guid')).toBe(newNotification.notificationGuid);
    expect(newRow.getAttribute('data-new')).toBe('true');
    expect(newRow.getAttribute('data-unread')).toBe('false');

    const olderUnreadRow = screen.getAllByTestId('mock-notification-row')
      .find(row => row.getAttribute('data-notification-guid') === olderUnreadNotification.notificationGuid);
    expect(olderUnreadRow?.getAttribute('data-new')).toBe('false');
    expect(olderUnreadRow?.getAttribute('data-unread')).toBe('true');
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

  it('opens actionable notification rows from the whole card surface', () => {
    const onNotificationOpen = vi.fn();
    render(<MobileNotificationsModal visible={true} onClose={() => {}} onNotificationOpen={onNotificationOpen} />);

    const firstRow = screen.getAllByTestId('mock-notification-row')[0]!;
    expect(firstRow.tagName).toBe('BUTTON');

    fireEvent.click(firstRow);

    expect(onNotificationOpen).toHaveBeenCalledWith(mockMobileNotifications[0]);
  });

  it('renders desktop presentation as a right-side drawer', () => {
    mockIsMobile.mockReturnValue(false);

    render(<MobileNotificationsModal visible={true} onClose={() => {}} presentation="desktopDrawer" onNotificationOpen={() => {}} />);

    const drawer = screen.getByTestId('desktop-notifications-drawer');
    expect(drawer).toBe(screen.getByRole('dialog', { name: 'Notifications' }));
    expect(drawer.getAttribute('data-modal-placement')).toBe('rightDrawer');
    expect(drawer.style.top).toBe('0px');
    expect(drawer.style.right).toBe('0px');
    expect(drawer.style.bottom).toBe('0px');
    expect(drawer.style.left).toBe('');
    expect(drawer.style.width).toBe('460px');
    expect(drawer.style.maxWidth).toBe('92vw');
    expect(drawer.style.borderTopLeftRadius).toBe('0px');
    expect(drawer.style.borderBottomLeftRadius).toBe('0px');
    screen.getAllByTestId('notification-title').forEach((title) => {
      expect(title.getAttribute('data-marquee-title')).toBe('true');
    });
    expect(screen.getByTestId('mobile-notifications-modal').getAttribute('data-notification-presentation')).toBe('desktopDrawer');
  });

  it('hides the chevron and leaves rows inert when no destination exists', () => {
    const onNotificationOpen = vi.fn();
    const notification: MobileNotification = {
      ...mockMobileNotifications[0]!,
      eventId: 99,
      notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0099',
      songId: undefined,
      instrument: undefined,
      navigation: null,
      eventKind: 'player_total_score_improved',
      payload: {
        coalescedEventCount: 1,
        coalescedEventKinds: ['player_total_score_improved'],
        coalescedEvents: [{ eventKind: 'player_total_score_improved', metric: 'score', oldNumeric: 1, newNumeric: 2 }],
      },
    };

    render(<MobileNotificationsModal visible={true} onClose={() => {}} notifications={[notification]} onNotificationOpen={onNotificationOpen} />);

    const row = screen.getByTestId('mock-notification-row');
    expect(row.tagName).toBe('ARTICLE');
    expect(row.getAttribute('data-actionable')).toBe('false');
    expect(screen.queryByTestId('notification-chevron')).toBeNull();

    fireEvent.click(row);

    expect(onNotificationOpen).not.toHaveBeenCalled();
  });

  it('keeps unread dots stable for the current modal session', () => {
    const unreadNotificationIds = new Set([mockMobileNotifications[0]!.notificationGuid]);
    const { rerender } = render(
      <MobileNotificationsModal visible={true} onClose={() => {}} unreadNotificationIds={unreadNotificationIds} onNotificationOpen={() => {}} />,
    );

    expect(screen.getAllByTestId('notification-unread-dot')).toHaveLength(1);
    expect(screen.getByTestId('notification-unread-dot').parentElement).toBe(screen.getAllByTestId('notification-chevron')[0]!.parentElement);
    expect(screen.getAllByTestId('notification-trailing-action')[0]!.style.justifyContent).toBe('center');
    expect(screen.getAllByTestId('notification-trailing-action')[0]!.style.position).toBe('relative');
    expect(screen.getByTestId('notification-unread-dot').style.position).toBe('absolute');
    expect(screen.getAllByTestId('mock-notification-row')[0]!.getAttribute('data-unread')).toBe('true');

    rerender(<MobileNotificationsModal visible={true} onClose={() => {}} unreadNotificationIds={new Set()} onNotificationOpen={() => {}} />);

    expect(screen.getAllByTestId('notification-unread-dot')).toHaveLength(1);

    rerender(<MobileNotificationsModal visible={false} onClose={() => {}} unreadNotificationIds={new Set()} onNotificationOpen={() => {}} />);
    rerender(<MobileNotificationsModal visible={true} onClose={() => {}} unreadNotificationIds={new Set()} onNotificationOpen={() => {}} />);

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