import { describe, expect, it } from 'vitest';
import { mockMobileNotifications } from '../../../src/components/notifications/MobileNotificationsModal';
import { getNotificationDestination, type NotificationDestinationInput } from '../../../src/components/notifications/notificationDestination';
import { getNotificationRankingMetric } from '../../../src/components/notifications/notificationRanking';

const APPLE_SONG_ID = 'e90125a8-742a-4be9-baa0-4d93f5fba556';
const STAND_AND_FIGHT_REMIX_SONG_ID = '4e5b8da5-0891-4a5b-9386-85031fcdca08';
const GHOSTS_N_STUFF_SONG_ID = 'e60b07e6-065a-4059-a7a4-4a88fe268108';

describe('getNotificationDestination', () => {
  it('routes solo song improvements to the song detail with the instrument selected', () => {
    const destination = getNotificationDestination(mockMobileNotifications[0]!);

    expect(destination?.path).toBe(`/songs/${APPLE_SONG_ID}?instrument=Solo_Drums`);
    expect(destination?.state).toEqual({ autoScroll: true });
  });

  it('keeps coalesced song score and song-rank notifications focused on the song', () => {
    const destination = getNotificationDestination(mockMobileNotifications[0]!);

    expect(destination?.path).toContain(`/songs/${APPLE_SONG_ID}`);
    expect(destination?.path).not.toContain('/leaderboards');
  });

  it('routes band song improvements with the played-instrument filter context', () => {
    const destination = getNotificationDestination(mockMobileNotifications[5]!);

    expect(destination?.path).toBe(`/songs/${APPLE_SONG_ID}`);
    expect(destination?.bandProfile).toEqual(expect.objectContaining({
      type: 'band',
      bandType: 'Band_Trios',
      displayName: 'SFentonX + kahnyri + db9342',
    }));
    expect(destination?.bandFilter).toEqual(expect.objectContaining({
      bandType: 'Band_Trios',
      comboId: 'Solo_Bass+Solo_Bass+Solo_Drums',
    }));
    expect(destination?.bandFilter?.assignments.map(assignment => assignment.instrument)).toEqual(['Solo_Bass', 'Solo_Bass', 'Solo_Drums']);
  });

  it('uses real catalog song ids for every hardcoded song notification', () => {
    expect(getNotificationDestination(mockMobileNotifications[0]!)?.path).toContain(`/songs/${APPLE_SONG_ID}`);
    expect(getNotificationDestination(mockMobileNotifications[1]!)?.path).toContain(`/songs/${STAND_AND_FIGHT_REMIX_SONG_ID}`);
    expect(getNotificationDestination(mockMobileNotifications[2]!)?.path).toContain(`/songs/${GHOSTS_N_STUFF_SONG_ID}`);
    expect(getNotificationDestination(mockMobileNotifications[5]!)?.path).toBe(`/songs/${APPLE_SONG_ID}`);
  });

  it('routes rank improvements to the leaderboards overview with the matching rank mode', () => {
    const destination = getNotificationDestination(mockMobileNotifications[3]!);

    expect(destination?.path).toBe('/leaderboards?rankBy=weighted');
    expect(destination?.rankBy).toBe('weighted');
  });

  it('maps rank metrics when no explicit navigation rank is present', () => {
    const notification: NotificationDestinationInput = {
      eventKind: 'player_total_score_rank_improved',
      metric: 'total_score_rank',
    };

    expect(getNotificationDestination(notification)?.path).toBe('/leaderboards?rankBy=totalscore');
  });

  it('maps aggregate notification rank metrics without treating song ranks as aggregate ranks', () => {
    expect(getNotificationRankingMetric({ eventKind: 'player_total_score_rank_improved', metric: 'total_score_rank' })).toBe('totalscore');
    expect(getNotificationRankingMetric({ eventKind: 'player_weighted_rank_improved', metric: 'weighted_rank' })).toBe('weighted');
    expect(getNotificationRankingMetric({ eventKind: 'player_skill_rank_improved', metric: 'adjusted_skill_rank' })).toBe('adjusted');
    expect(getNotificationRankingMetric({ eventKind: 'player_fc_rate_rank_improved', metric: 'fc_rate_rank' })).toBe('fcrate');
    expect(getNotificationRankingMetric({ eventKind: 'player_song_rank_improved', metric: 'song_rank' })).toBeNull();
  });

  it('routes multi-instrument song improvements without selecting an arbitrary instrument', () => {
    const notification: NotificationDestinationInput = {
      eventKind: 'player_score_pb',
      songId: APPLE_SONG_ID,
      instrument: 'Solo_Guitar',
      payload: {
        coalescedEvents: [
          { eventKind: 'player_score_pb', instrument: 'Solo_Guitar', metric: 'score' },
          { eventKind: 'player_fc_achieved', instrument: 'Solo_Drums', metric: 'full_combo' },
        ],
      },
    };

    expect(getNotificationDestination(notification)?.path).toBe(`/songs/${APPLE_SONG_ID}`);
  });

  it('returns null for notifications without a supported destination', () => {
    const notification: NotificationDestinationInput = {
      eventKind: 'player_total_score_improved',
      metric: 'score',
    };

    expect(getNotificationDestination(notification)).toBeNull();
  });
});
