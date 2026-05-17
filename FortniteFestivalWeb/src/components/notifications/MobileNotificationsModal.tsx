/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties, type MutableRefObject, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { IoChevronForward, IoNotificationsOffOutline } from 'react-icons/io5';
import ModalShell from '../modals/components/ModalShell';
import {
  Colors, Font, Weight, Gap, Radius, Border, LineHeight,
  Display, Align, Justify, Overflow, BoxSizing, TextTransform, ObjectFit,
  Opacity, FADE_DURATION, DEMO_SWAP_INTERVAL_MS,
  flexColumn, flexRow, border, padding,
} from '@festival/theme';
import { SERVER_INSTRUMENT_KEYS, serverInstrumentLabel, type ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { isExperimentalRankingMetric } from '../../pages/leaderboards/helpers/rankingHelpers';
import { InstrumentIcon } from '../display/InstrumentIcons';
import MarqueeText from '../common/MarqueeText';
import PressableButton from '../common/PressableButton';
import { getNotificationDestination, type NotificationNavigationContext } from './notificationDestination';
import { getNotificationRankingMetric, isAggregateRankNotificationEvent } from './notificationRanking';
import { formatNotificationPresentation, type NotificationFlagGroup, type NotificationFlagKind, type NotificationMessagePart, type NotificationPresentation, type NotificationTextEvent, type NotificationTextInput } from './notificationText';

const NOTIFICATION_MODAL_DESKTOP: CSSProperties = { width: 460, height: 640, maxHeight: '90vh' };
const NOTIFICATION_DESKTOP_DRAWER: CSSProperties = { width: 460, maxWidth: '92vw' };
const MODAL_TRANSITION_MS = 250;
const MEDIA_RAIL_SIZE = 64;
const MEDIA_ART_SIZE = 54;
const SONG_GRID_ART_SIZE = 44;
const SONG_GRID_ICON_SIZE = 18;
const FLAG_GROUP_ICON_SIZE = 20;
const SOLO_ICON_SIZE = 36;
const STACKED_ICON_SIZE = 26;
const GRID_ICON_SIZE = 24;
const MEDIA_CYCLE_SWAP_INTERVAL_MS = DEMO_SWAP_INTERVAL_MS;
const MEDIA_CYCLE_FADE_MS = FADE_DURATION;
const MEDIA_CYCLE_DURATION_SECONDS = (MEDIA_CYCLE_SWAP_INTERVAL_MS * 2) / 1000;
const MEDIA_CYCLE_EPOCH = 1_700_000_000_000;
const ALBUM_ART_PREFIX = 'https://cdn2.unrealengine.com/';
const MEDIA_CYCLE_STYLES = `
@media (prefers-reduced-motion: reduce) {
  [data-media-cycle-row-layer] { transition: none !important; transform: translateY(0) !important; }
}
`;
const BRIGHT_TEXT = '#ffffff';
const UNREAD_DOT_COLOR = '#facc15';
const LIST_FADE_EDGE_SIZE = 18;
const LIST_TOP_PADDING = LIST_FADE_EDGE_SIZE + Gap.sm;
const LIST_SCROLL_FADE = `linear-gradient(to bottom, transparent 0, #000 ${LIST_FADE_EDGE_SIZE}px, #000 calc(100% - ${LIST_FADE_EDGE_SIZE}px), transparent 100%)`;
const LIST_PADDING = `${LIST_TOP_PADDING}px 0 calc(${Gap.section}px + env(safe-area-inset-bottom, 0px))`;
const VISIBLE_SEEN_THRESHOLD = 0.9;
const EMPTY_UNREAD_IDS = new Set<string>();
const EMPTY_NEW_IDS = new Set<string>();
const NOOP_SEEN_HANDLER = () => {};
const SFENTONX_ACCOUNT_ID = '195e93ef108143b2975ee46662d4d0e1';
const KAHNYRI_ACCOUNT_ID = '4c2a1300df4c49a9b9d2b352d704bdf0';
const THIRD_BAND_ACCOUNT_ID = 'db9342c9dd874c799b58f177ec899f5e';
const APPLE_SONG_ID = 'e90125a8-742a-4be9-baa0-4d93f5fba556';
const STAND_AND_FIGHT_REMIX_SONG_ID = '4e5b8da5-0891-4a5b-9386-85031fcdca08';
const GHOSTS_N_STUFF_SONG_ID = 'e60b07e6-065a-4059-a7a4-4a88fe268108';
const FLAG_COLORS: Record<NotificationFlagKind, string> = {
  improvement: '#4b5563',
  firstPlay: '#6d28d9',
  newHighScore: '#0f766e',
  fullCombo: '#7c2d12',
  rankUp: '#1d4ed8',
  goldStars: '#92400e',
  starsUp: '#be123c',
  difficultyUp: '#047857',
  progress: '#4338ca',
};

type NotificationMedia =
  | { kind: 'song'; albumArt: string; alt: string }
  | { kind: 'songInstrumentGrid'; albumArt: string; alt: string; instruments: ServerInstrumentKey[]; label: string }
  | { kind: 'soloInstrument'; instrument: ServerInstrumentKey; label: string }
  | { kind: 'instrumentCombo'; instruments: ServerInstrumentKey[]; label: string; cycleAlbumArt?: { albumArt: string; alt: string } };

type NotificationMediaCycleLayer = 'icons' | 'art';

export type MobileNotification = NotificationTextInput & {
  eventId: number;
  notificationGuid: string;
  detectedAt: string;
  eventKind: string;
  songId?: string;
  instrument?: ServerInstrumentKey;
  title: string;
  context: string;
  detectedLabel: string;
  media: NotificationMedia;
  surfaceInstruments?: ServerInstrumentKey[];
  navigation?: NotificationNavigationContext | null;
  payload: {
    coalescedEventCount: number;
    coalescedEventKinds: string[];
    coalescedInstruments?: ServerInstrumentKey[];
    coalescedEvents: NotificationTextEvent[];
    oldFullCombo?: boolean | null;
    newFullCombo?: boolean | null;
    oldStars?: number | null;
    newStars?: number | null;
  };
};

const MOCK_NOTIFICATIONS: MobileNotification[] = [
  {
    eventId: 1,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0001',
    detectedAt: '2026-05-09T14:53:00Z',
    eventKind: 'player_score_pb',
    songId: APPLE_SONG_ID,
    instrument: 'Solo_Drums',
    title: 'Apple',
    songTitle: 'Apple',
    instrumentLabel: 'Pro Drums',
    context: 'SFentonX - Pro Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('tg9ervxpjbz6zww6-512x512-16b50aeec442.jpg'), alt: 'Apple album art' },
    navigation: { songId: APPLE_SONG_ID, instrument: 'Solo_Drums' },
    payload: {
      coalescedEventCount: 4,
      coalescedEventKinds: ['player_score_pb', 'player_gold_stars_achieved', 'player_stars_improved', 'player_song_rank_improved'],
      coalescedEvents: [
        { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 127025, newNumeric: 137700 },
        { eventKind: 'player_gold_stars_achieved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
        { eventKind: 'player_stars_improved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
        { eventKind: 'player_song_rank_improved', metric: 'song_rank', oldRank: 1214, newRank: 982 },
      ],
    },
  },
  {
    eventId: 2,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0002',
    detectedAt: '2026-05-09T14:52:00Z',
    eventKind: 'player_score_pb',
    songId: STAND_AND_FIGHT_REMIX_SONG_ID,
    instrument: 'Solo_Drums',
    title: 'Stand and Fight (Remix)',
    songTitle: 'Stand and Fight (Remix)',
    instrumentLabel: 'Drums',
    context: 'SFentonX - Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('9yu2qyo48olhpmev-512x512-ed189e21217f.jpg'), alt: 'Stand and Fight (Remix) album art' },
    navigation: { songId: STAND_AND_FIGHT_REMIX_SONG_ID, instrument: 'Solo_Drums' },
    payload: {
      coalescedEventCount: 3,
      coalescedEventKinds: ['player_score_pb', 'player_fc_achieved', 'player_song_rank_improved'],
      coalescedEvents: [
        { eventKind: 'player_score_pb', metric: 'score', oldNumeric: 126384, newNumeric: 126978 },
        { eventKind: 'player_fc_achieved', metric: 'full_combo' },
        { eventKind: 'player_song_rank_improved', metric: 'song_rank', oldRank: 442, newRank: 391 },
      ],
    },
  },
  {
    eventId: 3,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0003',
    detectedAt: '2026-05-09T14:51:00Z',
    eventKind: 'player_first_score',
    songId: GHOSTS_N_STUFF_SONG_ID,
    instrument: 'Solo_Drums',
    title: "Ghosts 'n' Stuff",
    songTitle: "Ghosts 'n' Stuff",
    instrumentLabel: 'Pro Drums',
    context: 'SFentonX - Pro Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('brc3mquv0rvjdlhz-512x512-cfb9e6ab2c73.jpg'), alt: "Ghosts 'n' Stuff album art" },
    navigation: { songId: GHOSTS_N_STUFF_SONG_ID, instrument: 'Solo_Drums' },
    payload: {
      coalescedEventCount: 2,
      coalescedEventKinds: ['player_first_score', 'player_gold_stars_achieved'],
      coalescedEvents: [
        { eventKind: 'player_first_score', metric: 'score', newNumeric: 180005, newRank: 1288 },
        { eventKind: 'player_gold_stars_achieved', metric: 'stars', newNumeric: 6 },
      ],
    },
  },
  {
    eventId: 4,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0004',
    detectedAt: '2026-05-09T14:50:00Z',
    eventKind: 'player_weighted_rank_improved',
    title: 'Solo Drums weighted percentile rank',
    instrumentLabel: 'Drums',
    context: 'SFentonX - Rankings',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'soloInstrument', instrument: 'Solo_Drums', label: 'Drums' },
    navigation: { rankBy: 'weighted' },
    payload: {
      coalescedEventCount: 1,
      coalescedEventKinds: ['player_weighted_rank_improved'],
      coalescedEvents: [
        { eventKind: 'player_weighted_rank_improved', metric: 'weighted_rank', oldRank: 45, newRank: 42 },
      ],
    },
  },
  {
    eventId: 5,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0005',
    detectedAt: '2026-05-09T14:49:00Z',
    eventKind: 'band_weighted_rank_improved',
    title: 'Band Duos weighted percentile rank',
    scopeLabel: 'Band Duos',
    context: 'SFentonX + kahnyri - Guitar/Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'instrumentCombo', instruments: ['Solo_Guitar', 'Solo_Drums'], label: 'Guitar/Drums' },
    navigation: { rankBy: 'weighted' },
    payload: {
      coalescedEventCount: 1,
      coalescedEventKinds: ['band_weighted_rank_improved'],
      coalescedEvents: [
        { eventKind: 'band_weighted_rank_improved', metric: 'weighted_rank', oldRank: 19, newRank: 16 },
      ],
    },
  },
  {
    eventId: 6,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0006',
    detectedAt: '2026-05-09T14:48:00Z',
    eventKind: 'band_score_pb',
    songId: APPLE_SONG_ID,
    title: 'Apple',
    songTitle: 'Apple',
    rankingScope: 'overall',
    comboLabel: 'Bass/Bass/Drums',
    scopeLabel: 'Band Trios',
    context: 'SFentonX + kahnyri + db9342 - Bass/Bass/Drums',
    detectedLabel: 'Today 7:53 AM',
    media: {
      kind: 'instrumentCombo',
      instruments: ['Solo_Bass', 'Solo_Bass', 'Solo_Drums'],
      label: 'Bass/Bass/Drums',
      cycleAlbumArt: { albumArt: albumArt('tg9ervxpjbz6zww6-512x512-16b50aeec442.jpg'), alt: 'Apple band notification album art' },
    },
    navigation: {
      songId: APPLE_SONG_ID,
      band: {
        bandId: 'notification-band-trios-apple',
        bandType: 'Band_Trios',
        teamKey: `${SFENTONX_ACCOUNT_ID}:${KAHNYRI_ACCOUNT_ID}:${THIRD_BAND_ACCOUNT_ID}`,
        displayName: 'SFentonX + kahnyri + db9342',
        members: [
          { accountId: SFENTONX_ACCOUNT_ID, displayName: 'SFentonX' },
          { accountId: KAHNYRI_ACCOUNT_ID, displayName: 'kahnyri' },
          { accountId: THIRD_BAND_ACCOUNT_ID, displayName: 'db9342' },
        ],
      },
      bandFilter: {
        comboId: 'Solo_Bass+Solo_Bass+Solo_Drums',
        assignments: [
          { accountId: SFENTONX_ACCOUNT_ID, instrument: 'Solo_Bass' },
          { accountId: KAHNYRI_ACCOUNT_ID, instrument: 'Solo_Bass' },
          { accountId: THIRD_BAND_ACCOUNT_ID, instrument: 'Solo_Drums' },
        ],
      },
    },
    payload: {
      coalescedEventCount: 5,
      coalescedEventKinds: ['band_score_pb', 'band_fc_achieved', 'band_gold_stars_achieved', 'band_song_rank_improved', 'band_song_rank_improved'],
      coalescedEvents: [
        { eventKind: 'band_score_pb', metric: 'score', oldNumeric: 1210400, newNumeric: 1234567 },
        { eventKind: 'band_fc_achieved', metric: 'full_combo' },
        { eventKind: 'band_gold_stars_achieved', metric: 'stars', oldNumeric: 5, newNumeric: 6 },
        { eventKind: 'band_song_rank_improved', metric: 'song_rank', oldRank: 42, newRank: 31, rankingScope: 'overall', scopeLabel: 'Band Trios' },
        { eventKind: 'band_song_rank_improved', metric: 'song_rank', oldRank: 9, newRank: 6, rankingScope: 'combo', comboLabel: 'Bass/Bass/Drums' },
      ],
    },
  },
];

export const mockMobileNotifications = MOCK_NOTIFICATIONS;
export const mockEmptyMobileNotifications: MobileNotification[] = [];

export type NotificationInstrumentFilter = ReadonlySet<ServerInstrumentKey> | null | undefined;
export type NotificationSurfaceFilter = NotificationInstrumentFilter | {
  visibleInstruments?: NotificationInstrumentFilter;
  enableExperimentalRanks?: boolean;
};

export function notificationSurfaceInstrument(
  notification: Pick<MobileNotification, 'instrument' | 'media' | 'surfaceInstruments'>,
): ServerInstrumentKey | null {
  return notificationSurfaceInstruments(notification)[0] ?? null;
}

export function notificationSurfaceInstruments(
  notification: Pick<MobileNotification, 'instrument' | 'media' | 'surfaceInstruments'>,
): ServerInstrumentKey[] {
  if (notification.surfaceInstruments?.length) return orderedUniqueInstruments(notification.surfaceInstruments);
  if (notification.instrument) return [notification.instrument];
  if (notification.media.kind === 'soloInstrument') return [notification.media.instrument];
  if (notification.media.kind === 'songInstrumentGrid') return orderedUniqueInstruments(notification.media.instruments);
  return [];
}

export function shouldSurfaceNotification(
  notification: MobileNotification,
  surfaceFilter: NotificationSurfaceFilter,
): boolean {
  return Boolean(projectSurfaceNotification(notification, surfaceFilter));
}

export function filterSurfaceNotifications(
  notifications: readonly MobileNotification[],
  surfaceFilter: NotificationSurfaceFilter,
): MobileNotification[] {
  return notifications.flatMap((notification) => {
    const projected = projectSurfaceNotification(notification, surfaceFilter);
    return projected ? [projected] : [];
  });
}

function projectSurfaceNotification(
  notification: MobileNotification,
  surfaceFilter: NotificationSurfaceFilter,
): MobileNotification | null {
  const filter = normalizeSurfaceFilter(surfaceFilter);
  const rankProjected = projectExperimentalRankNotification(notification, filter.enableExperimentalRanks);
  if (!rankProjected) return null;
  if (!filter.visibleInstruments) return rankProjected;

  const instruments = notificationSurfaceInstruments(rankProjected);
  if (instruments.length === 0) return rankProjected;
  return instruments.some(instrument => filter.visibleInstruments?.has(instrument)) ? rankProjected : null;
}

function normalizeSurfaceFilter(surfaceFilter: NotificationSurfaceFilter): { visibleInstruments: NotificationInstrumentFilter; enableExperimentalRanks: boolean } {
  if (!isStructuredSurfaceFilter(surfaceFilter)) {
    return { visibleInstruments: surfaceFilter, enableExperimentalRanks: true };
  }
  return {
    visibleInstruments: surfaceFilter.visibleInstruments,
    enableExperimentalRanks: surfaceFilter.enableExperimentalRanks ?? true,
  };
}

function isStructuredSurfaceFilter(surfaceFilter: NotificationSurfaceFilter): surfaceFilter is Exclude<NotificationSurfaceFilter, NotificationInstrumentFilter> {
  return Boolean(surfaceFilter) && !(surfaceFilter instanceof Set);
}

function projectExperimentalRankNotification(notification: MobileNotification, enableExperimentalRanks: boolean): MobileNotification | null {
  if (enableExperimentalRanks) return notification;

  const events = notification.payload.coalescedEvents.length > 0
    ? notification.payload.coalescedEvents
    : [{ eventKind: notification.eventKind, metric: notification.metric, oldRank: notification.oldRank, newRank: notification.newRank }];
  const hasAggregateRankEvents = events.some(isAggregateRankNotificationEvent);
  if (!hasAggregateRankEvents) return notification;

  const visibleEvents = events.filter((event) => {
    const metric = getNotificationRankingMetric(event);
    return !metric || !isExperimentalRankingMetric(metric);
  });
  if (visibleEvents.length === 0) return null;
  if (visibleEvents.length === events.length) return notification;

  const primary = visibleEvents[0]!;
  const rankBy = getNotificationRankingMetric(primary);
  return {
    ...notification,
    eventKind: primary.eventKind,
    metric: primary.metric,
    oldNumeric: primary.oldNumeric,
    newNumeric: primary.newNumeric,
    oldRank: primary.oldRank,
    newRank: primary.newRank,
    navigation: notification.navigation || rankBy
      ? { ...notification.navigation, rankBy }
      : notification.navigation,
    payload: {
      ...notification.payload,
      coalescedEventCount: visibleEvents.length,
      coalescedEventKinds: visibleEvents.map(event => event.eventKind),
      coalescedEvents: visibleEvents,
    },
  };
}

function orderedUniqueInstruments(instruments: readonly ServerInstrumentKey[]): ServerInstrumentKey[] {
  const present = new Set(instruments);
  return SERVER_INSTRUMENT_KEYS.filter(instrument => present.has(instrument));
}

type MobileNotificationsModalProps = {
  visible: boolean;
  onClose: () => void;
  presentation?: 'mobileModal' | 'desktopDrawer';
  notifications?: readonly MobileNotification[];
  unreadNotificationIds?: ReadonlySet<string>;
  newNotificationIds?: ReadonlySet<string>;
  notificationsGenerated?: boolean;
  onNotificationsSeen?: (notificationGuids: string[]) => void;
  onNotificationOpen?: (notification: MobileNotification) => void;
};

export default function MobileNotificationsModal({
  visible,
  onClose,
  presentation = 'mobileModal',
  notifications = MOCK_NOTIFICATIONS,
  unreadNotificationIds = EMPTY_UNREAD_IDS,
  newNotificationIds = EMPTY_NEW_IDS,
  notificationsGenerated = true,
  onNotificationsSeen = NOOP_SEEN_HANDLER,
  onNotificationOpen,
}: MobileNotificationsModalProps) {
  const { t } = useTranslation();
  const styles = useStyles();
  const listRef = useRef<HTMLDivElement | null>(null);
  const [listElement, setListElement] = useState<HTMLDivElement | null>(null);
  const rowRefs = useRef(new Map<string, HTMLElement>());
  const seenThisOpenRef = useRef(new Set<string>());
  const wasVisibleRef = useRef(false);
  const sortedNotifications = useMemo(() => sortNotificationsNewestFirst(notifications), [notifications]);
  const notificationSections = useMemo(() => partitionNotificationSections(sortedNotifications, newNotificationIds), [newNotificationIds, sortedNotifications]);
  const hasNotifications = sortedNotifications.length > 0;
  const [sessionUnreadIds, setSessionUnreadIds] = useState(() => new Set(unreadNotificationIds));
  const [isReadyForVisibility, setIsReadyForVisibility] = useState(false);
  const isDesktopDrawer = presentation === 'desktopDrawer';
  const setListRef = useCallback((element: HTMLDivElement | null) => {
    listRef.current = element;
    setListElement(element);
  }, []);

  const handleOpenComplete = useCallback(() => {
    setIsReadyForVisibility(true);
  }, []);

  useEffect(() => {
    if (visible && !wasVisibleRef.current) {
      seenThisOpenRef.current = new Set();
      setSessionUnreadIds(new Set(unreadNotificationIds));
      setIsReadyForVisibility(false);
    }
    if (!visible && wasVisibleRef.current) {
      seenThisOpenRef.current = new Set();
      setIsReadyForVisibility(false);
    }
    wasVisibleRef.current = visible;
  }, [unreadNotificationIds, visible]);

  const reportVisibleNotifications = useCallback(() => {
    if (!visible || !isReadyForVisibility) return;
    const list = listElement;
    if (!list) return;

    const viewportRect = list.getBoundingClientRect();
    const visibleNotificationIds: string[] = [];

    for (const notification of sortedNotifications) {
      const id = notification.notificationGuid;
      if (!unreadNotificationIds.has(id) || seenThisOpenRef.current.has(id)) continue;

      const row = rowRefs.current.get(id);
      if (!row) continue;
      if (visibleRatio(row.getBoundingClientRect(), viewportRect) < VISIBLE_SEEN_THRESHOLD) continue;

      seenThisOpenRef.current.add(id);
      visibleNotificationIds.push(id);
    }

    if (visibleNotificationIds.length > 0) onNotificationsSeen(visibleNotificationIds);
  }, [isReadyForVisibility, listElement, onNotificationsSeen, sortedNotifications, unreadNotificationIds, visible]);

  useEffect(() => {
    if (!visible || !isReadyForVisibility) return;
    const list = listElement;
    if (!list) return;

    let frame = 0;
    const scheduleVisibilityCheck = () => {
      if (frame) cancelAnimationFrame(frame);
      frame = requestAnimationFrame(() => {
        frame = 0;
        reportVisibleNotifications();
      });
    };

    scheduleVisibilityCheck();
    list.addEventListener('scroll', scheduleVisibilityCheck, { passive: true });
    window.addEventListener('resize', scheduleVisibilityCheck);
    return () => {
      if (frame) cancelAnimationFrame(frame);
      list.removeEventListener('scroll', scheduleVisibilityCheck);
      window.removeEventListener('resize', scheduleVisibilityCheck);
    };
  }, [isReadyForVisibility, listElement, reportVisibleNotifications, visible]);

  return (
    <ModalShell
      visible={visible}
      title={t('notifications.title')}
      onClose={onClose}
      desktopStyle={isDesktopDrawer ? NOTIFICATION_DESKTOP_DRAWER : NOTIFICATION_MODAL_DESKTOP}
      desktopPlacement={isDesktopDrawer ? 'rightDrawer' : 'center'}
      panelTestId={isDesktopDrawer ? 'desktop-notifications-drawer' : undefined}
      transitionMs={MODAL_TRANSITION_MS}
      onOpenComplete={handleOpenComplete}
    >
      <NotificationMediaCycleStyles />
      <div style={styles.body} data-testid="mobile-notifications-modal" data-notification-presentation={presentation}>
        <div ref={setListRef} style={styles.list} data-testid="notification-list" data-scroll-fade="true" data-scroll-fade-top-padding={LIST_TOP_PADDING} data-safe-area-bottom="true">
          {!hasNotifications ? <NotificationEmptyState styles={styles} t={t} notificationsGenerated={notificationsGenerated} /> : notificationSections.map((section) => (
            <section key={section.key} style={styles.section} data-testid="notification-section" data-notification-section={section.key}>
              <div style={styles.sectionHeading} data-testid="notification-section-heading">{t(section.labelKey)}</div>
              {section.notifications.map((notification) => (
                <NotificationRow
                  key={notification.notificationGuid}
                  notification={notification}
                  isSessionUnread={sessionUnreadIds.has(notification.notificationGuid)}
                  isNew={newNotificationIds.has(notification.notificationGuid)}
                  rowRefs={rowRefs}
                  styles={styles}
                  onNotificationOpen={onNotificationOpen}
                  t={t}
                />
              ))}
            </section>
          ))}
        </div>
      </div>
    </ModalShell>
  );
}

function NotificationEmptyState({ styles, t, notificationsGenerated }: { styles: ReturnType<typeof useStyles>; t: ReturnType<typeof useTranslation>['t']; notificationsGenerated: boolean }) {
  return (
    <div style={styles.emptyState} data-testid="notification-empty-state">
      <IoNotificationsOffOutline size={48} style={styles.emptyIcon} data-testid="notification-empty-icon" aria-hidden="true" />
      <div style={styles.emptyTitle}>{t('notifications.empty.title')}</div>
      <div style={styles.emptyBody}>{t(notificationsGenerated ? 'notifications.empty.generatedBody' : 'notifications.empty.notGeneratedBody')}</div>
    </div>
  );
}

function NotificationRow({
  notification,
  isSessionUnread,
  isNew,
  rowRefs,
  styles,
  onNotificationOpen,
  t,
}: {
  notification: MobileNotification;
  isSessionUnread: boolean;
  isNew: boolean;
  rowRefs: MutableRefObject<Map<string, HTMLElement>>;
  styles: ReturnType<typeof useStyles>;
  onNotificationOpen?: (notification: MobileNotification) => void;
  t: ReturnType<typeof useTranslation>['t'];
}) {
  const presentation = formatNotificationPresentation(t, notification);
  const canOpenNotification = Boolean(onNotificationOpen && getNotificationDestination(notification));
  const rowContent = (
    <>
      <NotificationMediaRail media={notification.media} styles={styles} />
      <div style={styles.content}>
        <div style={styles.titleRow}>
          <div style={styles.title} data-testid="notification-title" data-marquee-title="true">
            <MarqueeText text={presentation.title} as="span" style={styles.titleMarquee} />
          </div>
        </div>
        <div style={styles.summary} data-testid="notification-summary">
          {presentation.messageParts.map((part, index) => (
            <NotificationMessageSpan key={`${notification.eventId}-${index}`} part={part} styles={styles} />
          ))}
        </div>
        <NotificationFlags presentation={presentation} styles={styles} />
      </div>
      {(isSessionUnread || canOpenNotification) && (
        <div style={styles.trailingAction} data-testid="notification-trailing-action">
          {isSessionUnread && <span style={styles.unreadDot} data-testid="notification-unread-dot" aria-hidden="true" />}
          {canOpenNotification && <IoChevronForward size={18} aria-hidden="true" style={styles.chevron} data-testid="notification-chevron" />}
        </div>
      )}
    </>
  );
  const rowProps = {
    ref: (element: HTMLElement | null) => {
      if (element) rowRefs.current.set(notification.notificationGuid, element);
      else rowRefs.current.delete(notification.notificationGuid);
    },
    style: canOpenNotification ? { ...styles.row, ...styles.rowButton } : styles.row,
    'data-testid': 'mock-notification-row',
    'data-notification-guid': notification.notificationGuid,
    'data-detected-at': notification.detectedAt,
    'data-event-kind': notification.eventKind,
    'data-unread': isSessionUnread ? 'true' : 'false',
    'data-new': isNew ? 'true' : 'false',
    'data-actionable': canOpenNotification ? 'true' : 'false',
    'aria-label': canOpenNotification ? `${presentation.accessibilityLabel}. Open notification.` : presentation.accessibilityLabel,
  };

  return canOpenNotification ? (
    <PressableButton
      onPress={() => onNotificationOpen?.(notification)}
      {...rowProps}
    >
      {rowContent}
    </PressableButton>
  ) : (
    <article {...rowProps}>
      {rowContent}
    </article>
  );
}

function NotificationMessageSpan({ part, styles }: { part: NotificationMessagePart; styles: ReturnType<typeof useStyles> }) {
  return (
    <span style={part.emphasis ? styles.summaryEmphasis : undefined} data-notification-emphasis={part.emphasis ? 'true' : undefined}>
      {part.text}
    </span>
  );
}

function NotificationFlags({ presentation, styles }: { presentation: NotificationPresentation; styles: ReturnType<typeof useStyles> }) {
  if (presentation.flagGroups?.length) {
    return (
      <div style={styles.flagGroups} data-testid="notification-flag-groups">
        {presentation.flagGroups.map((group) => (
          <NotificationFlagGroupRow key={group.instrument} group={group} styles={styles} />
        ))}
      </div>
    );
  }

  return (
    <div style={styles.flags} data-testid="notification-flags">
      {presentation.flags.map((flag) => (
        <span key={flag.kind} style={{ ...styles.flag, backgroundColor: FLAG_COLORS[flag.kind] }} data-testid="notification-flag">
          {flag.label}
        </span>
      ))}
    </div>
  );
}

function NotificationFlagGroupRow({ group, styles }: { group: NotificationFlagGroup; styles: ReturnType<typeof useStyles> }) {
  return (
    <div
      style={styles.flagGroup}
      data-testid="notification-flag-group"
      data-instrument={group.instrument}
      aria-label={`${group.label}: ${group.flags.map(flag => flag.label).join(', ')}`}
    >
      <span style={styles.flagGroupIcon} aria-hidden="true">
        <InstrumentIcon instrument={group.instrument} size={FLAG_GROUP_ICON_SIZE} />
      </span>
      <div style={styles.flagGroupPills}>
        {group.flags.map((flag) => (
          <span key={flag.kind} style={{ ...styles.flag, backgroundColor: FLAG_COLORS[flag.kind] }} data-testid="notification-flag">
            {flag.label}
          </span>
        ))}
      </div>
    </div>
  );
}

function NotificationMediaCycleStyles() {
  return <style data-testid="notification-media-cycle-styles">{MEDIA_CYCLE_STYLES}</style>;
}

function NotificationMediaRail({ media, styles }: { media: NotificationMedia; styles: ReturnType<typeof useStyles> }) {
  if (media.kind === 'song') {
    return (
      <div style={styles.mediaRail} data-testid="notification-media-rail" data-media-kind="song" data-media-size={MEDIA_RAIL_SIZE}>
        <img src={media.albumArt} alt={media.alt} loading="lazy" style={styles.mediaArt} />
      </div>
    );
  }

  if (media.kind === 'songInstrumentGrid') {
    return (
      <div
        style={styles.songInstrumentMediaRail}
        data-testid="notification-media-rail"
        data-media-kind="songInstrumentGrid"
        data-media-size={MEDIA_RAIL_SIZE}
        aria-label={`Affected instruments: ${media.instruments.map(serverInstrumentLabel).join(', ')}`}
      >
        <img src={media.albumArt} alt={media.alt} loading="lazy" style={styles.songInstrumentMediaArt} />
        <div style={styles.songInstrumentGrid} data-testid="notification-media-song-instrument-grid" aria-hidden="true">
          {media.instruments.map((instrument) => (
            <InstrumentIcon key={instrument} instrument={instrument} size={SONG_GRID_ICON_SIZE} />
          ))}
        </div>
      </div>
    );
  }

  if (media.kind === 'soloInstrument') {
    return (
      <div style={styles.mediaRail} data-testid="notification-media-rail" data-media-kind="soloInstrument" data-media-size={MEDIA_RAIL_SIZE} aria-label={media.label}>
        <InstrumentIcon instrument={media.instrument} size={SOLO_ICON_SIZE} />
      </div>
    );
  }

  const layout = media.instruments.length === 2 ? 'duoStack' : 'comboGrid';
  const comboContent = layout === 'duoStack' ? (
    <div style={styles.duoStack} data-testid="notification-media-duo-stack">
      {media.instruments.map((instrument, index) => (
        <InstrumentIcon key={`${instrument}-${index}`} instrument={instrument} size={STACKED_ICON_SIZE} />
      ))}
    </div>
  ) : (
    <div style={styles.comboGrid} data-testid="notification-media-combo-grid">
      {Array.from({ length: 4 }).map((_, index) => {
        const instrument = media.instruments[index];
        return instrument
          ? <InstrumentIcon key={`${instrument}-${index}`} instrument={instrument} size={GRID_ICON_SIZE} />
          : <span key={`empty-${index}`} aria-hidden="true" />;
      })}
    </div>
  );

  if (media.cycleAlbumArt) {
    return (
      <div style={styles.mediaRail} data-testid="notification-media-rail" data-media-kind="instrumentCombo" data-media-layout={layout} data-media-size={MEDIA_RAIL_SIZE} aria-label={media.label}>
        <NotificationMediaCycle comboContent={comboContent} cycleAlbumArt={media.cycleAlbumArt} styles={styles} />
      </div>
    );
  }

  return (
    <div style={styles.mediaRail} data-testid="notification-media-rail" data-media-kind="instrumentCombo" data-media-layout={layout} data-media-size={MEDIA_RAIL_SIZE} aria-label={media.label}>
      {comboContent}
    </div>
  );
}

function NotificationMediaCycle({ comboContent, cycleAlbumArt, styles }: { comboContent: ReactNode; cycleAlbumArt: { albumArt: string; alt: string }; styles: ReturnType<typeof useStyles> }) {
  const [visibleLayer, setVisibleLayer] = useState<NotificationMediaCycleLayer>(() => mediaCycleLayerAt(Date.now()));
  const [isFading, setIsFading] = useState(false);

  useEffect(() => {
    let firstSwapTimer: ReturnType<typeof setTimeout> | undefined;
    let fadeTimer: ReturnType<typeof setTimeout> | undefined;
    let swapInterval: ReturnType<typeof setInterval> | undefined;

    const swapLayer = () => {
      setIsFading(true);
      fadeTimer = setTimeout(() => {
        setVisibleLayer(layer => layer === 'icons' ? 'art' : 'icons');
        setIsFading(false);
      }, MEDIA_CYCLE_FADE_MS);
    };

    firstSwapTimer = setTimeout(() => {
      swapLayer();
      swapInterval = setInterval(swapLayer, MEDIA_CYCLE_SWAP_INTERVAL_MS);
    }, mediaCycleDelayUntilNextSwap(Date.now()));

    return () => {
      if (firstSwapTimer) clearTimeout(firstSwapTimer);
      if (fadeTimer) clearTimeout(fadeTimer);
      if (swapInterval) clearInterval(swapInterval);
    };
  }, []);

  const iconsStyle = visibleLayer === 'icons'
    ? isFading ? styles.mediaCycleFading : styles.mediaCycleVisible
    : styles.mediaCycleHidden;
  const artStyle = visibleLayer === 'art'
    ? isFading ? styles.mediaCycleFading : styles.mediaCycleVisible
    : styles.mediaCycleHidden;

  return (
    <div
      style={styles.mediaCycle}
      data-testid="notification-media-cycle"
      data-media-cycle="freRowSwap"
      data-media-cycle-style="rowReplace"
      data-media-cycle-epoch={MEDIA_CYCLE_EPOCH}
      data-media-cycle-duration={MEDIA_CYCLE_DURATION_SECONDS}
      data-media-cycle-swap-interval={MEDIA_CYCLE_SWAP_INTERVAL_MS}
      data-media-cycle-fade-ms={MEDIA_CYCLE_FADE_MS}
      data-media-cycle-active-layer={visibleLayer}
      data-media-cycle-fading={isFading ? 'true' : 'false'}
    >
      <div style={{ ...styles.mediaCycleLayer, ...iconsStyle }} data-testid="notification-media-cycle-icons" data-media-cycle-row-layer="icons" aria-hidden="true">
        {comboContent}
      </div>
      <div style={{ ...styles.mediaCycleLayer, ...artStyle }} data-testid="notification-media-cycle-art" data-media-cycle-row-layer="art" aria-hidden="true">
        <img src={cycleAlbumArt.albumArt} alt={cycleAlbumArt.alt} loading="lazy" style={styles.mediaArt} />
      </div>
    </div>
  );
}

function mediaCycleLayerAt(timeMs: number): NotificationMediaCycleLayer {
  const elapsed = Math.max(0, timeMs - MEDIA_CYCLE_EPOCH);
  return Math.floor(elapsed / MEDIA_CYCLE_SWAP_INTERVAL_MS) % 2 === 0 ? 'icons' : 'art';
}

function mediaCycleDelayUntilNextSwap(timeMs: number): number {
  const elapsed = Math.max(0, timeMs - MEDIA_CYCLE_EPOCH);
  const progress = elapsed % MEDIA_CYCLE_SWAP_INTERVAL_MS;
  return progress === 0 ? MEDIA_CYCLE_SWAP_INTERVAL_MS : MEDIA_CYCLE_SWAP_INTERVAL_MS - progress;
}

function albumArt(path: string) {
  return `${ALBUM_ART_PREFIX}${path}`;
}

function notificationTime(notification: Pick<MobileNotification, 'detectedAt'>): number {
  const time = Date.parse(notification.detectedAt);
  return Number.isFinite(time) ? time : 0;
}

export function sortNotificationsNewestFirst<T extends Pick<MobileNotification, 'detectedAt' | 'eventId'>>(notifications: readonly T[]): T[] {
  return [...notifications].sort((left, right) => {
    const timeDelta = notificationTime(right) - notificationTime(left);
    if (timeDelta !== 0) return timeDelta;
    return right.eventId - left.eventId;
  });
}

function partitionNotificationSections(
  notifications: readonly MobileNotification[],
  newNotificationIds: ReadonlySet<string>,
): Array<{ key: 'new' | 'older'; labelKey: 'notifications.sections.new' | 'notifications.sections.older'; notifications: MobileNotification[] }> {
  const newNotifications = notifications.filter(notification => newNotificationIds.has(notification.notificationGuid));
  const olderNotifications = notifications.filter(notification => !newNotificationIds.has(notification.notificationGuid));
  const sections: Array<{ key: 'new' | 'older'; labelKey: 'notifications.sections.new' | 'notifications.sections.older'; notifications: MobileNotification[] }> = [];

  if (newNotifications.length > 0) {
    sections.push({ key: 'new', labelKey: 'notifications.sections.new', notifications: newNotifications });
  }
  if (olderNotifications.length > 0 || sections.length === 0) {
    sections.push({ key: 'older', labelKey: 'notifications.sections.older', notifications: olderNotifications });
  }

  return sections;
}

function visibleRatio(rowRect: DOMRect, viewportRect: DOMRect): number {
  if (rowRect.height <= 0) return 0;
  const visibleTop = Math.max(rowRect.top, viewportRect.top);
  const visibleBottom = Math.min(rowRect.bottom, viewportRect.bottom);
  return Math.max(0, visibleBottom - visibleTop) / rowRect.height;
}

function useStyles() {
  return useMemo(() => ({
    body: {
      ...flexColumn,
      flex: 1,
      minHeight: 0,
      overflow: Overflow.hidden,
      padding: padding(0, Gap.section, Gap.section),
    } as CSSProperties,
    list: {
      ...flexColumn,
      gap: Gap.md,
      flex: 1,
      minHeight: 0,
      overflowY: Overflow.auto,
      padding: LIST_PADDING,
      maskImage: LIST_SCROLL_FADE,
      WebkitMaskImage: LIST_SCROLL_FADE,
      maskSize: '100% 100%',
      WebkitMaskSize: '100% 100%',
    } as CSSProperties,
    section: {
      ...flexColumn,
      gap: Gap.sm,
      minWidth: 0,
    } as CSSProperties,
    sectionHeading: {
      color: 'rgba(255, 255, 255, 0.74)',
      fontSize: Font.xs,
      fontWeight: Weight.semibold,
      lineHeight: LineHeight.snug,
      letterSpacing: 0,
      textTransform: TextTransform.uppercase,
      padding: padding(0, Gap.xs),
    } as CSSProperties,
    emptyState: {
      ...flexColumn,
      alignItems: Align.center,
      justifyContent: Justify.center,
      gap: Gap.sm,
      flex: 1,
      minHeight: 240,
      padding: padding(Gap.section, Gap.xl),
      textAlign: 'center',
      color: BRIGHT_TEXT,
    } as CSSProperties,
    emptyTitle: {
      color: BRIGHT_TEXT,
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      lineHeight: LineHeight.snug,
      letterSpacing: 0,
    } as CSSProperties,
    emptyIcon: {
      color: 'rgba(255, 255, 255, 0.72)',
      flexShrink: 0,
    } as CSSProperties,
    emptyBody: {
      color: 'rgba(255, 255, 255, 0.68)',
      fontSize: Font.sm,
      lineHeight: LineHeight.snug,
      maxWidth: 240,
      letterSpacing: 0,
    } as CSSProperties,
    row: {
      ...flexRow,
      alignItems: Align.center,
      gap: Gap.md,
      padding: Gap.lg,
      borderRadius: Radius.sm,
      background: Colors.surfaceSubtle,
      border: border(Border.thin, Colors.borderSubtle),
    } as CSSProperties,
    rowButton: {
      width: '100%',
      appearance: 'none',
      WebkitAppearance: 'none',
      font: 'inherit',
      color: 'inherit',
      textAlign: 'left',
      cursor: 'pointer',
    } as CSSProperties,
    mediaRail: {
      ...flexRow,
      position: 'relative',
      alignItems: Align.center,
      justifyContent: Justify.center,
      width: MEDIA_RAIL_SIZE,
      height: MEDIA_RAIL_SIZE,
      minWidth: MEDIA_RAIL_SIZE,
      minHeight: MEDIA_RAIL_SIZE,
      flexShrink: 0,
      overflow: Overflow.hidden,
      boxSizing: BoxSizing.borderBox,
    } as CSSProperties,
    mediaCycle: {
      position: 'relative',
      width: MEDIA_RAIL_SIZE,
      height: MEDIA_RAIL_SIZE,
      display: Display.block,
    } as CSSProperties,
    mediaCycleLayer: {
      ...flexRow,
      position: 'absolute',
      inset: 0,
      alignItems: Align.center,
      justifyContent: Justify.center,
      width: MEDIA_RAIL_SIZE,
      height: MEDIA_RAIL_SIZE,
      willChange: 'opacity, transform',
    } as CSSProperties,
    mediaCycleVisible: {
      transition: `opacity ${MEDIA_CYCLE_FADE_MS}ms ease, transform ${MEDIA_CYCLE_FADE_MS}ms ease`,
      opacity: 1,
      transform: 'translateY(0)',
    } as CSSProperties,
    mediaCycleFading: {
      transition: `opacity ${MEDIA_CYCLE_FADE_MS}ms ease, transform ${MEDIA_CYCLE_FADE_MS}ms ease`,
      opacity: Opacity.none,
      transform: 'translateY(4px)',
    } as CSSProperties,
    mediaCycleHidden: {
      transition: `opacity ${MEDIA_CYCLE_FADE_MS}ms ease, transform ${MEDIA_CYCLE_FADE_MS}ms ease`,
      opacity: Opacity.none,
      transform: 'translateY(4px)',
    } as CSSProperties,
    mediaArt: {
      width: MEDIA_ART_SIZE,
      height: MEDIA_ART_SIZE,
      borderRadius: Radius.xs,
      objectFit: ObjectFit.cover,
      display: Display.block,
    } as CSSProperties,
    songInstrumentMediaRail: {
      ...flexColumn,
      position: 'relative',
      alignItems: Align.center,
      justifyContent: Justify.center,
      gap: 4,
      width: MEDIA_RAIL_SIZE,
      minWidth: MEDIA_RAIL_SIZE,
      flexShrink: 0,
      overflow: Overflow.hidden,
      boxSizing: BoxSizing.borderBox,
    } as CSSProperties,
    songInstrumentMediaArt: {
      width: SONG_GRID_ART_SIZE,
      height: SONG_GRID_ART_SIZE,
      borderRadius: Radius.xs,
      objectFit: ObjectFit.cover,
      display: Display.block,
      flexShrink: 0,
    } as CSSProperties,
    songInstrumentGrid: {
      display: Display.grid,
      gridTemplateColumns: `${SONG_GRID_ICON_SIZE}px ${SONG_GRID_ICON_SIZE}px`,
      gridAutoRows: `${SONG_GRID_ICON_SIZE}px`,
      gap: 3,
      alignItems: Align.center,
      justifyContent: Justify.center,
      flexShrink: 0,
    } as CSSProperties,
    duoStack: {
      ...flexColumn,
      alignItems: Align.center,
      justifyContent: Justify.center,
      gap: 2,
    } as CSSProperties,
    comboGrid: {
      display: Display.grid,
      gridTemplateColumns: `${GRID_ICON_SIZE}px ${GRID_ICON_SIZE}px`,
      gridTemplateRows: `${GRID_ICON_SIZE}px ${GRID_ICON_SIZE}px`,
      gap: 4,
      alignItems: Align.center,
      justifyContent: Justify.center,
    } as CSSProperties,
    content: {
      ...flexColumn,
      gap: Gap.sm,
      minWidth: 0,
      flex: 1,
    } as CSSProperties,
    chevron: {
      color: 'rgba(255, 255, 255, 0.72)',
      flexShrink: 0,
      alignSelf: Align.center,
    } as CSSProperties,
    trailingAction: {
      ...flexRow,
      position: 'relative',
      alignItems: Align.center,
      justifyContent: Justify.center,
      width: 20,
      minWidth: 20,
      alignSelf: Align.stretch,
      flexShrink: 0,
      marginLeft: Gap.xs,
    } as CSSProperties,
    titleRow: {
      ...flexRow,
      alignItems: Align.start,
      gap: Gap.sm,
      minWidth: 0,
    } as CSSProperties,
    title: {
      color: BRIGHT_TEXT,
      fontSize: Font.lg,
      fontWeight: Weight.bold,
      lineHeight: 1.2,
      minWidth: 0,
      flex: 1,
    } as CSSProperties,
    titleMarquee: {
      width: '100%',
      color: 'inherit',
      fontSize: 'inherit',
      fontWeight: 'inherit',
      lineHeight: 'inherit',
      minWidth: 0,
    } as CSSProperties,
    unreadDot: {
      position: 'absolute',
      top: 'calc(50% - 24px)',
      left: '50%',
      transform: 'translateX(-50%)',
      width: 9,
      height: 9,
      minWidth: 9,
      borderRadius: '50%',
      background: UNREAD_DOT_COLOR,
      boxShadow: `0 0 0 2px ${Colors.surfaceSubtle}`,
      flexShrink: 0,
    } as CSSProperties,
    summary: {
      color: BRIGHT_TEXT,
      fontSize: Font.sm,
      lineHeight: LineHeight.snug,
      whiteSpace: 'pre-line',
    } as CSSProperties,
    summaryEmphasis: {
      fontWeight: Weight.bold,
    } as CSSProperties,
    flags: {
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xs,
      flexWrap: 'wrap',
    } as CSSProperties,
    flagGroups: {
      ...flexColumn,
      gap: Gap.xs,
      minWidth: 0,
    } as CSSProperties,
    flagGroup: {
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xs,
      minWidth: 0,
    } as CSSProperties,
    flagGroupIcon: {
      width: FLAG_GROUP_ICON_SIZE,
      height: FLAG_GROUP_ICON_SIZE,
      minWidth: FLAG_GROUP_ICON_SIZE,
      display: Display.flex,
      alignItems: Align.center,
      justifyContent: Justify.center,
      flexShrink: 0,
    } as CSSProperties,
    flagGroupPills: {
      display: Display.flex,
      alignItems: Align.center,
      gap: Gap.xs,
      flexWrap: 'wrap',
      minWidth: 0,
    } as CSSProperties,
    flag: {
      flexShrink: 0,
      padding: padding(Gap.xs, Gap.sm),
      borderRadius: Radius.xs,
      color: BRIGHT_TEXT,
      fontSize: Font.sm,
      fontWeight: Weight.semibold,
      lineHeight: '16px',
      letterSpacing: 0,
      textTransform: TextTransform.none,
      border: border(Border.thick, 'rgba(255, 255, 255, 0.18)'),
      boxSizing: BoxSizing.borderBox,
    } as CSSProperties,
  }), []);
}
