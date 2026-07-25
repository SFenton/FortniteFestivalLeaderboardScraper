import { SERVER_INSTRUMENT_KEYS, type ServerInstrumentKey } from '@festival/core/api';
import { isExperimentalRankingMetric } from '../../pages/leaderboards/helpers/rankingHelpers';
import { getNotificationRankingMetric, isAggregateRankNotificationEvent } from './notificationRanking';
import type { MobileNotification } from './notificationTypes';

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

function normalizeSurfaceFilter(surfaceFilter: NotificationSurfaceFilter): {
  visibleInstruments: NotificationInstrumentFilter;
  enableExperimentalRanks: boolean;
} {
  if (!isStructuredSurfaceFilter(surfaceFilter)) {
    return { visibleInstruments: surfaceFilter, enableExperimentalRanks: true };
  }
  return {
    visibleInstruments: surfaceFilter.visibleInstruments,
    enableExperimentalRanks: surfaceFilter.enableExperimentalRanks ?? true,
  };
}

function isStructuredSurfaceFilter(
  surfaceFilter: NotificationSurfaceFilter,
): surfaceFilter is Exclude<NotificationSurfaceFilter, NotificationInstrumentFilter> {
  return Boolean(surfaceFilter) && !(surfaceFilter instanceof Set);
}

function projectExperimentalRankNotification(
  notification: MobileNotification,
  enableExperimentalRanks: boolean,
): MobileNotification | null {
  if (enableExperimentalRanks) return notification;

  const events = notification.payload.coalescedEvents.length > 0
    ? notification.payload.coalescedEvents
    : [{
        eventKind: notification.eventKind,
        metric: notification.metric,
        oldRank: notification.oldRank,
        newRank: notification.newRank,
      }];
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
