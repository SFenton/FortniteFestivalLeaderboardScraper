import type { RankingMetric, PlayerBandType, ServerInstrumentKey } from '@festival/core/api/serverTypes';
import { Routes } from '../../routes';
import type { SelectedBandMemberProfile, SelectedBandProfile } from '../../state/selectedProfile';
import type { AppliedBandComboFilter, BandInstrumentFilterAssignment } from '../../types/bandFilter';
import type { NotificationTextEvent } from './notificationText';
import { getNotificationRankingMetric } from './notificationRanking';

export type NotificationNavigationContext = {
  songId?: string | null;
  instrument?: ServerInstrumentKey | null;
  band?: NotificationBandContext | null;
  bandFilter?: NotificationBandFilterContext | null;
  rankBy?: RankingMetric | null;
};

export type NotificationBandContext = {
  bandId: string;
  bandType: PlayerBandType;
  teamKey: string;
  displayName: string;
  members?: SelectedBandMemberProfile[];
};

export type NotificationBandFilterContext = {
  comboId: string;
  assignments: BandInstrumentFilterAssignment[];
};

export type NotificationDestinationInput = {
  eventKind?: string | null;
  metric?: string | null;
  songId?: string | null;
  instrument?: ServerInstrumentKey | null;
  navigation?: NotificationNavigationContext | null;
  payload?: {
    coalescedEvents?: readonly NotificationTextEvent[] | null;
  } | null;
};

export type NotificationDestination = {
  path: string;
  state?: { autoScroll?: boolean };
  rankBy?: RankingMetric;
  bandProfile?: SelectedBandProfile;
  bandFilter?: AppliedBandComboFilter;
};

const SONG_EVENT_KINDS = new Set([
  'player_first_score',
  'player_score_pb',
  'player_song_rank_improved',
  'player_stars_improved',
  'player_gold_stars_achieved',
  'player_fc_achieved',
  'player_difficulty_bumped',
  'band_first_score',
  'band_score_pb',
  'band_combo_score_pb',
  'band_song_rank_improved',
  'band_stars_improved',
  'band_gold_stars_achieved',
  'band_fc_achieved',
  'band_member_difficulty_bumped',
]);

export function getNotificationDestination(notification: NotificationDestinationInput): NotificationDestination | null {
  const events = getDestinationEvents(notification);
  const songId = notification.navigation?.songId ?? notification.songId;
  if (songId && events.some(event => SONG_EVENT_KINDS.has(event.eventKind))) {
    return buildSongDestination(notification, songId);
  }

  const rankBy = notification.navigation?.rankBy ?? getRankMetric(events);
  if (rankBy) {
    return {
      path: withQuery(Routes.leaderboards, { rankBy }),
      rankBy,
    };
  }

  return null;
}

function buildSongDestination(notification: NotificationDestinationInput, songId: string): NotificationDestination {
  const instrument = hasMultipleEventInstruments(notification)
    ? null
    : notification.navigation?.instrument ?? notification.instrument;
  const bandProfile = buildBandProfile(notification.navigation?.band);
  const bandFilter = bandProfile ? buildBandFilter(bandProfile, notification.navigation?.bandFilter) : null;
  const path = instrument
    ? withQuery(Routes.songDetail(songId), { instrument })
    : Routes.songDetail(songId);

  return {
    path,
    ...(instrument ? { state: { autoScroll: true } } : {}),
    ...(bandProfile ? { bandProfile } : {}),
    ...(bandFilter ? { bandFilter } : {}),
  };
}

function buildBandProfile(band: NotificationBandContext | null | undefined): SelectedBandProfile | null {
  if (!band?.bandId || !band.bandType || !band.teamKey || !band.displayName) return null;
  return {
    type: 'band',
    bandId: band.bandId,
    bandType: band.bandType,
    teamKey: band.teamKey,
    displayName: band.displayName,
    members: band.members ?? [],
  };
}

function buildBandFilter(bandProfile: SelectedBandProfile, filter: NotificationBandFilterContext | null | undefined): AppliedBandComboFilter | null {
  if (!filter?.comboId || filter.assignments.length === 0) return null;
  return {
    bandId: bandProfile.bandId,
    bandType: bandProfile.bandType as PlayerBandType,
    teamKey: bandProfile.teamKey,
    comboId: filter.comboId,
    assignments: filter.assignments,
  };
}

function getDestinationEvents(notification: NotificationDestinationInput): NotificationTextEvent[] {
  const payloadEvents = notification.payload?.coalescedEvents?.filter(event => event.eventKind) ?? [];
  if (payloadEvents.length > 0) return [...payloadEvents];
  if (!notification.eventKind) return [];
  return [{ eventKind: notification.eventKind, metric: notification.metric }];
}

function getRankMetric(events: readonly NotificationTextEvent[]): RankingMetric | null {
  for (const event of events) {
    const metric = getNotificationRankingMetric(event);
    if (metric) return metric;
  }
  return null;
}

function hasMultipleEventInstruments(notification: NotificationDestinationInput): boolean {
  const instruments = new Set(
    getDestinationEvents(notification)
      .map(event => event.instrument)
      .filter((instrument): instrument is ServerInstrumentKey => Boolean(instrument)),
  );
  return instruments.size > 1;
}

function withQuery(path: string, params: Record<string, string>) {
  const query = new URLSearchParams(params).toString();
  return query ? `${path}?${query}` : path;
}
