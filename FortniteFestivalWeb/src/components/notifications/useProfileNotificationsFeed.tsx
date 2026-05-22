import { useCallback, useEffect, useMemo } from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { instrumentsFromComboId, isWithinGroupComboId } from '@festival/core/combos';
import { comboScopeLabel } from '../../utils/rankingScopes';
import { api } from '../../api/client';
import { useAppWebSocket } from '../../hooks/data/useAppWebSocket';
import type { SelectedProfile } from '../../hooks/data/useSelectedProfile';
import {
  DEFAULT_INSTRUMENT,
  SERVER_INSTRUMENT_KEYS,
  serverInstrumentLabel,
  type ImprovementNotificationDto,
  type ImprovementNotificationEventPayload,
  type ImprovementNotificationPayload,
  type ImprovementNotificationsEnvelope,
  type ServerInstrumentKey,
  type ServerSong,
  type WsNotificationMessage,
} from '@festival/core/api/serverTypes';
import { mockEmptyMobileNotifications, mockMobileNotifications, type MobileNotification } from './MobileNotificationsModal';
import { notificationFeedKeyForProfile } from './notificationSeenState';
import type { NotificationNavigationContext } from './notificationDestination';
import type { NotificationTextEvent } from './notificationText';

const NOTIFICATION_QUERY_ROOT = 'profileNotifications';
const NOTIFICATION_QUERY_LIMIT = 50;
const PROFILE_NOTIFICATION_MESSAGE_TYPES = new Set([
  'improvement_notifications_changed',
  'notifications_changed',
  'notification_feed_changed',
]);
const PROFILE_SYNC_COMPLETION_MESSAGE_TYPES = new Set([
  'backfill_complete',
  'history_recon_complete',
  'rivals_complete',
]);
const SERVICE_NEW_SHOP_SONG_KIND = 'service_new_shop_song';

export type NotificationGenerationStatus = 'generated' | 'notGenerated';
export type NotificationFeedStatus = 'idle' | 'loading' | 'ready' | 'error';

export type ProfileNotificationsFeed = {
  feedKey: string;
  notifications: readonly MobileNotification[];
  notificationIds: readonly string[];
  sourceVersion: string | null;
  generationStatus: NotificationGenerationStatus;
  status: NotificationFeedStatus;
};

type UseProfileNotificationsFeedOptions = {
  useMockData?: boolean;
  useEmptyMock?: boolean;
  mockSourceVersion?: string;
};

export function useProfileNotificationsFeed(
  profile: SelectedProfile | null,
  songs: readonly ServerSong[],
  options?: UseProfileNotificationsFeedOptions,
): ProfileNotificationsFeed {
  const useMockData = options?.useMockData ?? false;
  const useEmptyMock = options?.useEmptyMock ?? false;
  const feedKey = notificationFeedKeyForProfile(profile);
  const queryKey = useMemo(() => notificationFeedQueryKey(profile), [profile]);

  const query = useQuery({
    queryKey,
    queryFn: ({ signal }) => fetchNotificationEnvelope(profile!, signal),
    enabled: Boolean(profile) && !useMockData,
    staleTime: 60_000,
  });

  const songsById = useMemo(() => new Map(songs.map(song => [song.songId, song])), [songs]);

  const notifications = useMemo(() => {
    if (useMockData) return useEmptyMock ? mockEmptyMobileNotifications : mockMobileNotifications;
    if (!profile || !query.data) return [];
    return query.data.items.map(item => mapNotificationDto(item, profile, songsById));
  }, [profile, query.data, songsById, useEmptyMock, useMockData]);

  const sourceVersion = useMemo(() => {
    if (useMockData) return options?.mockSourceVersion ?? null;
    return notificationSourceVersion(query.data);
  }, [options?.mockSourceVersion, query.data, useMockData]);

  const generationStatus = useMemo<NotificationGenerationStatus>(() => {
    if (useMockData) return 'generated';
    return notificationsGenerated(query.data) ? 'generated' : 'notGenerated';
  }, [query.data, useMockData]);

  const status: NotificationFeedStatus = !profile && !useMockData
    ? 'idle'
    : query.isLoading && !useMockData
      ? 'loading'
      : query.isError && !useMockData
        ? 'error'
        : 'ready';

  return useMemo(() => ({
    feedKey,
    notifications,
    notificationIds: notifications.map(notification => notification.notificationGuid),
    sourceVersion,
    generationStatus,
    status,
  }), [feedKey, generationStatus, notifications, sourceVersion, status]);
}

export function NotificationFeedWebSocketBridge({ profile }: { profile: SelectedProfile }) {
  const queryClient = useQueryClient();
  const { subscribe } = useAppWebSocket();
  const queryKey = useMemo(() => notificationFeedQueryKey(profile), [profile]);

  const invalidateFeed = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey });
  }, [queryClient, queryKey]);

  useEffect(() => subscribe((message) => {
    if (notificationMessageMatchesProfile(message, profile)) invalidateFeed();
  }), [invalidateFeed, profile, subscribe]);

  return null;
}

function notificationFeedQueryKey(profile: SelectedProfile | null) {
  if (!profile) return [NOTIFICATION_QUERY_ROOT, 'none'] as const;
  return profile.type === 'player'
    ? [NOTIFICATION_QUERY_ROOT, 'player', profile.accountId] as const
    : [NOTIFICATION_QUERY_ROOT, 'band', profile.bandId, profile.bandType, profile.teamKey] as const;
}

function fetchNotificationEnvelope(profile: SelectedProfile, signal?: AbortSignal): Promise<ImprovementNotificationsEnvelope> {
  if (profile.type === 'player') {
    return api.getPlayerNotifications(profile.accountId, NOTIFICATION_QUERY_LIMIT, { signal });
  }
  return api.getBandNotificationsById(profile.bandId, NOTIFICATION_QUERY_LIMIT, { signal });
}

function notificationSourceVersion(envelope: ImprovementNotificationsEnvelope | undefined): string | null {
  if (!envelope) return null;
  if (envelope.sourceRunId != null) return String(envelope.sourceRunId);
  return envelope.sourceCompletedAt ?? null;
}

function notificationsGenerated(envelope: ImprovementNotificationsEnvelope | undefined): boolean {
  if (!envelope) return false;
  if (envelope.notificationsGenerated != null) return envelope.notificationsGenerated;
  return Boolean(envelope.sourceRunId != null || envelope.sourceCompletedAt || envelope.items.length > 0);
}

function mapNotificationDto(
  dto: ImprovementNotificationDto,
  profile: SelectedProfile,
  songsById: ReadonlyMap<string, ServerSong>,
): MobileNotification {
  if (dto.eventKind === SERVICE_NEW_SHOP_SONG_KIND) {
    return mapServiceNewShopSongNotificationDto(dto, profile, songsById);
  }
  const instrument = normalizeServerInstrument(dto.instrument);
  const instrumentLabel = instrument ? serverInstrumentLabel(instrument) : null;
  const song = dto.songId ? songsById.get(dto.songId) : undefined;
  const comboLabel = dto.comboId ? comboScopeLabel(dto.comboId) : null;
  const events = normalizedNotificationEvents(dto, comboLabel);
  const surfaceInstruments = notificationSurfaceInstruments(dto, events);
  const scopeLabel = dto.rankingScope === 'combo' && comboLabel
    ? comboLabel
    : profile.type === 'band'
      ? profile.displayName
      : instrumentLabel;

  return {
    eventId: dto.eventId,
    notificationGuid: dto.notificationGuid || `${profile.type}:${dto.eventId}`,
    detectedAt: dto.detectedAt,
    eventKind: dto.eventKind,
    songId: dto.songId ?? undefined,
    instrument,
    metric: dto.metric,
    oldNumeric: dto.oldNumeric,
    newNumeric: dto.newNumeric,
    oldRank: dto.oldRank,
    newRank: dto.newRank,
    rankingScope: dto.rankingScope,
    comboId: dto.comboId,
    comboLabel,
    scopeLabel,
    title: song?.title ?? notificationFallbackTitle(dto, instrumentLabel, comboLabel, profile),
    songTitle: song?.title,
    instrumentLabel,
    context: notificationContext(profile, instrumentLabel, comboLabel),
    detectedLabel: formatDetectedLabel(dto.detectedAt),
    media: notificationMedia(song, instrument, comboLabel, dto.comboId, surfaceInstruments),
    surfaceInstruments,
    navigation: notificationNavigation(dto, profile, instrument),
    payload: {
      coalescedEventCount: eventCount(dto.payload, events),
      coalescedEventKinds: eventKinds(dto.payload, events),
      coalescedInstruments: surfaceInstruments,
      coalescedEvents: events,
      oldFullCombo: booleanValue(dto.payload?.oldFullCombo),
      newFullCombo: booleanValue(dto.payload?.newFullCombo),
      oldStars: numberValue(dto.payload?.oldStars),
      newStars: numberValue(dto.payload?.newStars),
    },
  };
}

function mapServiceNewShopSongNotificationDto(
  dto: ImprovementNotificationDto,
  profile: SelectedProfile,
  songsById: ReadonlyMap<string, ServerSong>,
): MobileNotification {
  const song = dto.songId ? songsById.get(dto.songId) : undefined;
  const songTitle = stringValue(dto.payload?.songTitle) ?? song?.title ?? dto.songId ?? 'New Song';
  const artist = stringValue(dto.payload?.artist) ?? song?.artist ?? 'Unknown Artist';
  const albumArt = song?.albumArt ?? stringValue(dto.payload?.albumArt);

  return {
    eventId: dto.eventId,
    notificationGuid: dto.notificationGuid || `${profile.type}:service:${dto.eventId}`,
    detectedAt: dto.detectedAt,
    eventKind: dto.eventKind,
    songId: dto.songId ?? undefined,
    title: songTitle,
    songTitle,
    artist,
    context: 'Item Shop',
    detectedLabel: formatDetectedLabel(dto.detectedAt),
    media: albumArt
      ? { kind: 'song', albumArt, alt: `${songTitle} album art` }
      : { kind: 'soloInstrument', instrument: DEFAULT_INSTRUMENT, label: 'Item Shop' },
    navigation: dto.songId ? { songId: dto.songId } : null,
    payload: {
      coalescedEventCount: 1,
      coalescedEventKinds: [dto.eventKind],
      coalescedEvents: [{ eventKind: dto.eventKind }],
    },
  };
}

function normalizedNotificationEvents(dto: ImprovementNotificationDto, comboLabel: string | null): NotificationTextEvent[] {
  const payloadEvents = Array.isArray(dto.payload?.coalescedEvents)
    ? dto.payload.coalescedEvents.flatMap(event => normalizePayloadEvent(event, dto, comboLabel))
    : [];
  if (payloadEvents.length > 0) return payloadEvents;
  return [normalizeDtoEvent(dto, comboLabel)];
}

function normalizePayloadEvent(
  event: ImprovementNotificationEventPayload,
  dto: ImprovementNotificationDto,
  comboLabel: string | null,
): NotificationTextEvent[] {
  const eventKind = stringValue(event.eventKind);
  if (!eventKind) return [];
  const rankingScope = stringValue(event.rankingScope) ?? dto.rankingScope ?? null;
  const instrument = normalizeServerInstrument(event.instrument) ?? normalizeServerInstrument(dto.instrument) ?? null;
  return [{
    eventKind,
    instrument,
    instrumentLabel: instrument ? serverInstrumentLabel(instrument) : null,
    metric: stringValue(event.metric),
    oldNumeric: numberValue(event.oldNumeric),
    newNumeric: numberValue(event.newNumeric),
    oldRank: numberValue(event.oldRank),
    newRank: numberValue(event.newRank),
    oldLabel: stringValue(event.oldLabel),
    newLabel: stringValue(event.newLabel),
    oldFullCombo: booleanValue(event.oldFullCombo),
    newFullCombo: booleanValue(event.newFullCombo),
    oldStars: numberValue(event.oldStars),
    newStars: numberValue(event.newStars),
    comboLabel: stringValue(event.comboLabel) ?? comboLabel,
    scopeLabel: stringValue(event.scopeLabel) ?? (rankingScope === 'combo' ? comboLabel : null),
    rankingScope,
    scopeComboId: stringValue(event.scopeComboId) ?? dto.comboId ?? null,
    comboId: stringValue(event.comboId) ?? dto.comboId ?? null,
  }];
}

function normalizeDtoEvent(dto: ImprovementNotificationDto, comboLabel: string | null): NotificationTextEvent {
  const instrument = normalizeServerInstrument(dto.instrument) ?? null;
  return {
    eventKind: dto.eventKind,
    instrument,
    instrumentLabel: instrument ? serverInstrumentLabel(instrument) : null,
    metric: dto.metric,
    oldNumeric: dto.oldNumeric,
    newNumeric: dto.newNumeric,
    oldRank: dto.oldRank,
    newRank: dto.newRank,
    oldFullCombo: booleanValue(dto.payload?.oldFullCombo),
    newFullCombo: booleanValue(dto.payload?.newFullCombo),
    oldStars: numberValue(dto.payload?.oldStars),
    newStars: numberValue(dto.payload?.newStars),
    comboLabel,
    scopeLabel: dto.rankingScope === 'combo' ? comboLabel : null,
    rankingScope: dto.rankingScope,
    scopeComboId: dto.comboId,
    comboId: dto.comboId,
  };
}

function notificationSurfaceInstruments(
  dto: ImprovementNotificationDto,
  events: readonly NotificationTextEvent[],
): ServerInstrumentKey[] {
  const instruments = [
    ...(Array.isArray(dto.payload?.coalescedInstruments) ? dto.payload.coalescedInstruments : []),
    ...events.map(event => event.instrument),
    dto.instrument,
  ];

  return orderedUniqueInstruments(instruments.flatMap(instrument => {
    const normalized = normalizeServerInstrument(instrument);
    return normalized ? [normalized] : [];
  }));
}

function orderedUniqueInstruments(instruments: readonly ServerInstrumentKey[]): ServerInstrumentKey[] {
  const present = new Set(instruments);
  return SERVER_INSTRUMENT_KEYS.filter(instrument => present.has(instrument));
}

function eventCount(payload: ImprovementNotificationPayload | null | undefined, events: readonly NotificationTextEvent[]): number {
  const count = numberValue(payload?.coalescedEventCount);
  return count && count > 0 ? count : events.length;
}

function eventKinds(payload: ImprovementNotificationPayload | null | undefined, events: readonly NotificationTextEvent[]): string[] {
  return Array.isArray(payload?.coalescedEventKinds) && payload.coalescedEventKinds.length > 0
    ? payload.coalescedEventKinds.filter((kind): kind is string => typeof kind === 'string' && kind.trim().length > 0)
    : events.map(event => event.eventKind);
}

function notificationFallbackTitle(
  dto: ImprovementNotificationDto,
  instrumentLabel: string | null,
  comboLabel: string | null,
  profile: SelectedProfile,
): string {
  if (dto.songId) return dto.songId;
  if (comboLabel) return comboLabel;
  if (instrumentLabel) return instrumentLabel;
  return profile.displayName;
}

function notificationContext(profile: SelectedProfile, instrumentLabel: string | null, comboLabel: string | null): string {
  const detail = comboLabel ?? instrumentLabel;
  return detail ? `${profile.displayName} - ${detail}` : profile.displayName;
}

function notificationMedia(
  song: ServerSong | undefined,
  instrument: ServerInstrumentKey | undefined,
  comboLabel: string | null,
  comboId: string | null | undefined,
  surfaceInstruments: readonly ServerInstrumentKey[],
): MobileNotification['media'] {
  if (comboLabel) {
    const instruments = comboId && isWithinGroupComboId(comboId) ? instrumentsFromComboId(comboId) : instrumentsFromComboLabel(comboLabel);
    return {
      kind: 'instrumentCombo',
      instruments: instruments.length > 0 ? instruments : [DEFAULT_INSTRUMENT],
      label: comboLabel,
      ...(song?.albumArt ? { cycleAlbumArt: { albumArt: song.albumArt, alt: `${song.title} album art` } } : {}),
    };
  }

  if (song?.albumArt && surfaceInstruments.length > 1) {
    const label = surfaceInstruments.map(serverInstrumentLabel).join(', ');
    return { kind: 'songInstrumentGrid', albumArt: song.albumArt, alt: `${song.title} album art`, instruments: [...surfaceInstruments], label };
  }

  if (song?.albumArt) {
    return { kind: 'song', albumArt: song.albumArt, alt: `${song.title} album art` };
  }

  const fallbackInstrument = instrument ?? DEFAULT_INSTRUMENT;
  return { kind: 'soloInstrument', instrument: fallbackInstrument, label: serverInstrumentLabel(fallbackInstrument) };
}

function instrumentsFromComboLabel(comboLabel: string): ServerInstrumentKey[] {
  return comboLabel.split(/\s*[+/]\s*/)
    .map(label => SERVER_INSTRUMENT_KEYS.find(instrument => serverInstrumentLabel(instrument) === label.trim()))
    .filter((instrument): instrument is ServerInstrumentKey => Boolean(instrument));
}

function notificationNavigation(
  dto: ImprovementNotificationDto,
  profile: SelectedProfile,
  instrument: ServerInstrumentKey | undefined,
): NotificationNavigationContext | null {
  if (!dto.songId && !dto.metric && !dto.eventKind) return null;
  return {
    songId: dto.songId,
    instrument,
    band: profile.type === 'band'
      ? {
        bandId: profile.bandId,
        bandType: profile.bandType,
        teamKey: profile.teamKey,
        displayName: profile.displayName,
        members: profile.members,
      }
      : null,
  };
}

function formatDetectedLabel(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return '';
  return new Intl.DateTimeFormat(undefined, {
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(date);
}

function normalizeServerInstrument(value: unknown): ServerInstrumentKey | undefined {
  return typeof value === 'string' && SERVER_INSTRUMENT_KEYS.includes(value as ServerInstrumentKey)
    ? value as ServerInstrumentKey
    : undefined;
}

function stringValue(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim() : null;
}

function numberValue(value: unknown): number | null {
  if (typeof value === 'number' && Number.isFinite(value)) return value;
  if (typeof value !== 'string') return null;
  const parsed = Number(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function booleanValue(value: unknown): boolean | null {
  if (typeof value === 'boolean') return value;
  if (typeof value !== 'string') return null;
  const normalized = value.trim().toLowerCase();
  if (normalized === 'true') return true;
  if (normalized === 'false') return false;
  return null;
}

function notificationMessageMatchesProfile(message: WsNotificationMessage, profile: SelectedProfile): boolean {
  const record = message as Record<string, unknown>;
  const messageType = stringValue(record.type);
  if (!messageType) return false;

  if (PROFILE_NOTIFICATION_MESSAGE_TYPES.has(messageType)) {
    if (profile.type === 'player') return stringValue(record.accountId) === profile.accountId || record.accountId == null;
    return stringValue(record.bandId) === profile.bandId
      || (stringValue(record.bandType) === profile.bandType && stringValue(record.teamKey) === profile.teamKey)
      || (record.bandId == null && record.bandType == null && record.teamKey == null);
  }

  if (profile.type === 'player' && PROFILE_SYNC_COMPLETION_MESSAGE_TYPES.has(messageType)) return true;

  if (profile.type === 'player' && messageType === 'sync_progress') {
    const accountId = stringValue(record.accountId);
    const phase = stringValue(record.phase);
    return accountId === profile.accountId && (phase === 'complete' || phase === 'postscrape');
  }

  return false;
}