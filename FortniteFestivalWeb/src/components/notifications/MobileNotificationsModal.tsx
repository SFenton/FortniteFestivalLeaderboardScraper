/* eslint-disable react/forbid-dom-props -- useStyles pattern */
import { useCallback, useEffect, useMemo, useRef, useState, type CSSProperties } from 'react';
import { useTranslation } from 'react-i18next';
import ModalShell from '../modals/components/ModalShell';
import {
  Colors, Font, Weight, Gap, Radius, Border, LineHeight,
  Display, Align, Justify, Overflow, BoxSizing, TextTransform, ObjectFit,
  flexColumn, flexRow, border, padding,
} from '@festival/theme';
import type { ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { InstrumentIcon } from '../display/InstrumentIcons';
import { formatNotificationPresentation, type NotificationFlagKind, type NotificationMessagePart, type NotificationTextEvent, type NotificationTextInput } from './notificationText';

const NOTIFICATION_MODAL_DESKTOP: CSSProperties = { width: 460, height: 640, maxHeight: '90vh' };
const MODAL_TRANSITION_MS = 250;
const MEDIA_RAIL_SIZE = 64;
const MEDIA_ART_SIZE = 54;
const SOLO_ICON_SIZE = 36;
const STACKED_ICON_SIZE = 26;
const GRID_ICON_SIZE = 24;
const ALBUM_ART_PREFIX = 'https://cdn2.unrealengine.com/';
const BRIGHT_TEXT = '#ffffff';
const UNREAD_DOT_COLOR = '#facc15';
const LIST_FADE_EDGE_SIZE = 18;
const LIST_TOP_PADDING = LIST_FADE_EDGE_SIZE + Gap.sm;
const LIST_SCROLL_FADE = `linear-gradient(to bottom, transparent 0, #000 ${LIST_FADE_EDGE_SIZE}px, #000 calc(100% - ${LIST_FADE_EDGE_SIZE}px), transparent 100%)`;
const LIST_PADDING = `${LIST_TOP_PADDING}px 0 calc(${Gap.section}px + env(safe-area-inset-bottom, 0px))`;
const VISIBLE_SEEN_THRESHOLD = 0.9;
const EMPTY_UNREAD_IDS = new Set<string>();
const NOOP_SEEN_HANDLER = () => {};
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
  | { kind: 'soloInstrument'; instrument: ServerInstrumentKey; label: string }
  | { kind: 'instrumentCombo'; instruments: ServerInstrumentKey[]; label: string };

export type MobileNotification = NotificationTextInput & {
  eventId: number;
  notificationGuid: string;
  detectedAt: string;
  eventKind: string;
  title: string;
  context: string;
  detectedLabel: string;
  media: NotificationMedia;
  payload: {
    coalescedEventCount: number;
    coalescedEventKinds: string[];
    coalescedEvents: NotificationTextEvent[];
  };
};

const MOCK_NOTIFICATIONS: MobileNotification[] = [
  {
    eventId: 1,
    notificationGuid: 'f2ddf535-f63e-4fd3-9c2c-9b7273fd0001',
    detectedAt: '2026-05-09T14:53:00Z',
    eventKind: 'player_score_pb',
    title: 'Apple',
    songTitle: 'Apple',
    instrumentLabel: 'Pro Drums',
    context: 'SFentonX - Pro Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('tg9ervxpjbz6zww6-512x512-16b50aeec442.jpg'), alt: 'Apple album art' },
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
    title: 'Stand and Fight (Remix)',
    songTitle: 'Stand and Fight (Remix)',
    instrumentLabel: 'Drums',
    context: 'SFentonX - Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('9yu2qyo48olhpmev-512x512-ed189e21217f.jpg'), alt: 'Stand and Fight (Remix) album art' },
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
    title: "Ghosts 'n' Stuff",
    songTitle: "Ghosts 'n' Stuff",
    instrumentLabel: 'Pro Drums',
    context: 'SFentonX - Pro Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'song', albumArt: albumArt('brc3mquv0rvjdlhz-512x512-cfb9e6ab2c73.jpg'), alt: "Ghosts 'n' Stuff album art" },
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
    title: 'Solo Drums weighted rank',
    instrumentLabel: 'Drums',
    context: 'SFentonX - Rankings',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'soloInstrument', instrument: 'Solo_Drums', label: 'Drums' },
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
    title: 'Band Duos weighted rank',
    scopeLabel: 'Band Duos',
    context: 'SFentonX + kahnyri - Guitar/Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'instrumentCombo', instruments: ['Solo_Guitar', 'Solo_Drums'], label: 'Guitar/Drums' },
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
    title: 'Apple',
    songTitle: 'Apple',
    rankingScope: 'overall',
    comboLabel: 'Bass/Bass/Drums',
    scopeLabel: 'Band Trios',
    context: 'SFentonX + kahnyri + db9342 - Bass/Bass/Drums',
    detectedLabel: 'Today 7:53 AM',
    media: { kind: 'instrumentCombo', instruments: ['Solo_Bass', 'Solo_Bass', 'Solo_Drums'], label: 'Bass/Bass/Drums' },
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

type MobileNotificationsModalProps = {
  visible: boolean;
  onClose: () => void;
  notifications?: readonly MobileNotification[];
  unreadNotificationIds?: ReadonlySet<string>;
  onNotificationsSeen?: (notificationGuids: string[]) => void;
};

export default function MobileNotificationsModal({
  visible,
  onClose,
  notifications = MOCK_NOTIFICATIONS,
  unreadNotificationIds = EMPTY_UNREAD_IDS,
  onNotificationsSeen = NOOP_SEEN_HANDLER,
}: MobileNotificationsModalProps) {
  const { t } = useTranslation();
  const styles = useStyles();
  const listRef = useRef<HTMLDivElement | null>(null);
  const [listElement, setListElement] = useState<HTMLDivElement | null>(null);
  const rowRefs = useRef(new Map<string, HTMLElement>());
  const seenThisOpenRef = useRef(new Set<string>());
  const wasVisibleRef = useRef(false);
  const sortedNotifications = useMemo(() => sortNotificationsNewestFirst(notifications), [notifications]);
  const [sessionUnreadIds, setSessionUnreadIds] = useState(() => new Set(unreadNotificationIds));
  const [isReadyForVisibility, setIsReadyForVisibility] = useState(false);
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
      desktopStyle={NOTIFICATION_MODAL_DESKTOP}
      transitionMs={MODAL_TRANSITION_MS}
      onOpenComplete={handleOpenComplete}
    >
      <div style={styles.body} data-testid="mobile-notifications-modal">
        <div ref={setListRef} style={styles.list} data-testid="notification-list" data-scroll-fade="true" data-scroll-fade-top-padding={LIST_TOP_PADDING} data-safe-area-bottom="true">
          {sortedNotifications.map((notification) => {
            const presentation = formatNotificationPresentation(t, notification);
            const isSessionUnread = sessionUnreadIds.has(notification.notificationGuid);
            return (
              <article
                key={notification.notificationGuid}
                ref={(element) => {
                  if (element) rowRefs.current.set(notification.notificationGuid, element);
                  else rowRefs.current.delete(notification.notificationGuid);
                }}
                style={styles.row}
                data-testid="mock-notification-row"
                data-notification-guid={notification.notificationGuid}
                data-detected-at={notification.detectedAt}
                data-event-kind={notification.eventKind}
                data-unread={isSessionUnread ? 'true' : 'false'}
                aria-label={presentation.accessibilityLabel}
              >
                <NotificationMediaRail media={notification.media} styles={styles} />
                <div style={styles.content}>
                  <div style={styles.titleRow}>
                    <div style={styles.title} data-testid="notification-title">{presentation.title}</div>
                    {isSessionUnread && <span style={styles.unreadDot} data-testid="notification-unread-dot" aria-hidden="true" />}
                  </div>
                  <div style={styles.summary} data-testid="notification-summary">
                    {presentation.messageParts.map((part, index) => (
                      <NotificationMessageSpan key={`${notification.eventId}-${index}`} part={part} styles={styles} />
                    ))}
                  </div>
                  <div style={styles.flags}>
                    {presentation.flags.map((flag) => (
                      <span key={flag.kind} style={{ ...styles.flag, backgroundColor: FLAG_COLORS[flag.kind] }} data-testid="notification-flag">
                        {flag.label}
                      </span>
                    ))}
                  </div>
                </div>
              </article>
            );
          })}
        </div>
      </div>
    </ModalShell>
  );
}

function NotificationMessageSpan({ part, styles }: { part: NotificationMessagePart; styles: ReturnType<typeof useStyles> }) {
  return (
    <span style={part.emphasis ? styles.summaryEmphasis : undefined} data-notification-emphasis={part.emphasis ? 'true' : undefined}>
      {part.text}
    </span>
  );
}

function NotificationMediaRail({ media, styles }: { media: NotificationMedia; styles: ReturnType<typeof useStyles> }) {
  if (media.kind === 'song') {
    return (
      <div style={styles.mediaRail} data-testid="notification-media-rail" data-media-kind="song" data-media-size={MEDIA_RAIL_SIZE}>
        <img src={media.albumArt} alt={media.alt} loading="lazy" style={styles.mediaArt} />
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
  return (
    <div style={styles.mediaRail} data-testid="notification-media-rail" data-media-kind="instrumentCombo" data-media-layout={layout} data-media-size={MEDIA_RAIL_SIZE} aria-label={media.label}>
      {layout === 'duoStack' ? (
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
      )}
    </div>
  );
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
    row: {
      ...flexRow,
      alignItems: Align.center,
      gap: Gap.md,
      padding: Gap.lg,
      borderRadius: Radius.sm,
      background: Colors.surfaceSubtle,
      border: border(Border.thin, Colors.borderSubtle),
    } as CSSProperties,
    mediaRail: {
      ...flexRow,
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
    mediaArt: {
      width: MEDIA_ART_SIZE,
      height: MEDIA_ART_SIZE,
      borderRadius: Radius.xs,
      objectFit: ObjectFit.cover,
      display: Display.block,
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
    unreadDot: {
      width: 9,
      height: 9,
      minWidth: 9,
      marginTop: 5,
      borderRadius: '50%',
      background: UNREAD_DOT_COLOR,
      boxShadow: `0 0 0 2px ${Colors.surfaceSubtle}`,
      flexShrink: 0,
    } as CSSProperties,
    summary: {
      color: BRIGHT_TEXT,
      fontSize: Font.sm,
      lineHeight: LineHeight.snug,
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
