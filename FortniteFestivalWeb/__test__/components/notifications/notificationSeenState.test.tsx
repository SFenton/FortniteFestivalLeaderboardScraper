import { act, renderHook } from '@testing-library/react';
import { beforeEach, describe, expect, it } from 'vitest';
import {
  NOTIFICATION_MOCK_FEED_KEY,
  NOTIFICATION_SEEN_STORAGE_KEY,
  addSeenNotificationIds,
  countUnreadNotifications,
  deriveUnreadNotificationIds,
  notificationFeedKeyForProfile,
  pruneSeenNotificationIds,
  readSeenNotificationIds,
  useNotificationSeenState,
  writeSeenNotificationIds,
} from '../../../src/components/notifications/notificationSeenState';

function sortedSetValues(values: ReadonlySet<string>) {
  return Array.from(values).sort();
}

function storedSeenState() {
  return JSON.parse(localStorage.getItem(NOTIFICATION_SEEN_STORAGE_KEY) ?? '{}') as Record<string, string[]>;
}

describe('notificationSeenState', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('persists seen IDs per feed and prunes IDs that are no longer live', () => {
    writeSeenNotificationIds('player:p1', ['beta', 'alpha', 'alpha', ' ']);
    writeSeenNotificationIds('player:p2', ['other']);

    expect(sortedSetValues(readSeenNotificationIds('player:p1'))).toEqual(['alpha', 'beta']);

    const prunedIds = pruneSeenNotificationIds('player:p1', ['beta', 'gamma']);

    expect(sortedSetValues(prunedIds)).toEqual(['beta']);
    expect(storedSeenState()).toEqual({
      'player:p1': ['beta'],
      'player:p2': ['other'],
    });
  });

  it('adds only current notification IDs and derives unread IDs', () => {
    writeSeenNotificationIds('player:p1', ['alpha', 'expired']);

    const seenIds = addSeenNotificationIds('player:p1', ['beta', 'missing'], ['alpha', 'beta', 'gamma']);

    expect(sortedSetValues(seenIds)).toEqual(['alpha', 'beta']);
    expect(sortedSetValues(deriveUnreadNotificationIds(['alpha', 'beta', 'gamma'], seenIds))).toEqual(['gamma']);
    expect(countUnreadNotifications(['alpha', 'beta', 'gamma'], seenIds)).toBe(1);
  });

  it('normalizes corrupt storage and fallback feed keys', () => {
    localStorage.setItem(NOTIFICATION_SEEN_STORAGE_KEY, 'not-json');
    expect(sortedSetValues(readSeenNotificationIds('player:p1'))).toEqual([]);

    localStorage.setItem(NOTIFICATION_SEEN_STORAGE_KEY, JSON.stringify({
      valid: ['alpha', '', 42, 'alpha'],
      ignored: 'alpha',
      ' ': ['bad-feed'],
    }));

    expect(sortedSetValues(readSeenNotificationIds('valid'))).toEqual(['alpha']);

    writeSeenNotificationIds('', ['fallback']);
    expect(sortedSetValues(readSeenNotificationIds(NOTIFICATION_MOCK_FEED_KEY))).toEqual(['fallback']);
  });

  it('removes storage when a feed has no seen IDs left', () => {
    writeSeenNotificationIds('player:p1', ['alpha']);

    writeSeenNotificationIds('player:p1', []);

    expect(localStorage.getItem(NOTIFICATION_SEEN_STORAGE_KEY)).toBeNull();
  });

  it('supports unavailable storage without throwing', () => {
    expect(sortedSetValues(readSeenNotificationIds('player:p1', null))).toEqual([]);
    expect(sortedSetValues(writeSeenNotificationIds('player:p1', ['alpha'], null))).toEqual(['alpha']);
    expect(sortedSetValues(pruneSeenNotificationIds('player:p1', ['alpha'], null))).toEqual([]);
  });

  it('builds feed keys from the selected profile', () => {
    expect(notificationFeedKeyForProfile(null)).toBe(NOTIFICATION_MOCK_FEED_KEY);
    expect(notificationFeedKeyForProfile({ type: 'player', accountId: 'p1', displayName: 'Player' })).toBe('player:p1');
    expect(notificationFeedKeyForProfile({
      type: 'band',
      bandId: 'b1',
      bandType: 'Band_Duets',
      teamKey: 'team-key',
      displayName: 'Band',
      members: [],
    })).toBe('band:b1');
  });

  it('prunes on hook load and marks notifications as seen', () => {
    writeSeenNotificationIds('player:p1', ['old', 'alpha']);

    const { result, rerender } = renderHook(
      ({ currentIds }) => useNotificationSeenState('player:p1', currentIds),
      { initialProps: { currentIds: ['alpha', 'beta'] } },
    );

    expect(sortedSetValues(result.current.seenNotificationIds)).toEqual(['alpha']);
    expect(sortedSetValues(result.current.unreadNotificationIds)).toEqual(['beta']);
    expect(result.current.unreadCount).toBe(1);

    act(() => result.current.markNotificationsSeen(['beta']));

    expect(sortedSetValues(result.current.seenNotificationIds)).toEqual(['alpha', 'beta']);
    expect(result.current.unreadCount).toBe(0);
    expect(storedSeenState()).toEqual({ 'player:p1': ['alpha', 'beta'] });

    rerender({ currentIds: ['beta', 'gamma'] });

    expect(sortedSetValues(result.current.seenNotificationIds)).toEqual(['beta']);
    expect(sortedSetValues(result.current.unreadNotificationIds)).toEqual(['gamma']);
  });
});
