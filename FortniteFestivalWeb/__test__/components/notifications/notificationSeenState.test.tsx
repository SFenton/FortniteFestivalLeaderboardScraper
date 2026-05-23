import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  NOTIFICATION_FRESHNESS_STORAGE_KEY,
} from '../../../src/components/notifications/notificationFreshnessState';
import {
  NOTIFICATION_FEED_META_STORAGE_KEY,
  NOTIFICATION_FEED_RETENTION_MS,
  NOTIFICATION_MOCK_FEED_KEY,
  NOTIFICATION_SEEN_STORAGE_KEY,
  addSeenNotificationIds,
  countUnreadNotifications,
  deriveUnreadNotificationIds,
  notificationFeedKeyForProfile,
  pruneSeenNotificationIds,
  readNotificationFeedMetadata,
  readSeenNotificationIds,
  touchNotificationFeed,
  useNotificationSeenState,
  writeSeenNotificationIds,
} from '../../../src/components/notifications/notificationSeenState';

function sortedSetValues(values: ReadonlySet<string>) {
  return Array.from(values).sort();
}

function storedSeenState() {
  return JSON.parse(localStorage.getItem(NOTIFICATION_SEEN_STORAGE_KEY) ?? '{}') as Record<string, string[]>;
}

function storedFreshnessState() {
  return JSON.parse(localStorage.getItem(NOTIFICATION_FRESHNESS_STORAGE_KEY) ?? '{}') as Record<string, unknown>;
}

describe('notificationSeenState', () => {
  beforeEach(() => {
    vi.useRealTimers();
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
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

  it('touches active feeds and prunes abandoned feed state after retention', () => {
    const now = new Date('2026-05-09T16:00:00Z');
    vi.useFakeTimers();
    vi.setSystemTime(now);

    writeSeenNotificationIds('player:old', ['old-seen']);
    writeSeenNotificationIds('player:current', ['current-seen']);
    touchNotificationFeed('player:old', localStorage, now.getTime() - NOTIFICATION_FEED_RETENTION_MS - 1);

    writeSeenNotificationIds('player:current', ['current-seen', 'next-seen']);

    expect(storedSeenState()).toEqual({ 'player:current': ['current-seen', 'next-seen'] });
    expect(readNotificationFeedMetadata('player:current')?.lastAccessedAt).toBe(now.getTime());
    const metadata = JSON.parse(localStorage.getItem(NOTIFICATION_FEED_META_STORAGE_KEY) ?? '{}') as Record<string, unknown>;
    expect(metadata['player:old']).toBeUndefined();
  });

  it('prunes abandoned freshness state with abandoned seen state', () => {
    const now = new Date('2026-05-09T16:00:00Z');
    vi.useFakeTimers();
    vi.setSystemTime(now);

    writeSeenNotificationIds('player:old', ['old-seen']);
    writeSeenNotificationIds('player:current', ['current-seen']);
    localStorage.setItem(NOTIFICATION_FRESHNESS_STORAGE_KEY, JSON.stringify({
      'player:old': { knownNotificationIds: ['old-seen'], newNotificationIds: ['old-seen'], sourceVersion: '1' },
      'player:current': { knownNotificationIds: ['current-seen'], newNotificationIds: [], sourceVersion: '1' },
    }));
    touchNotificationFeed('player:old', localStorage, now.getTime() - NOTIFICATION_FEED_RETENTION_MS - 1);

    writeSeenNotificationIds('player:current', ['current-seen', 'next-seen']);

    expect(storedSeenState()).toEqual({ 'player:current': ['current-seen', 'next-seen'] });
    expect(storedFreshnessState()).toEqual({
      'player:current': { knownNotificationIds: ['current-seen'], newNotificationIds: [], sourceVersion: '1' },
    });
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

  it('keeps seen IDs while the current feed is still loading', () => {
    writeSeenNotificationIds('player:p1', ['old', 'alpha']);

    const { result, rerender } = renderHook(
      ({ currentIds, isCurrentFeedLoaded }) => useNotificationSeenState('player:p1', currentIds, { isCurrentFeedLoaded }),
      { initialProps: { currentIds: [] as string[], isCurrentFeedLoaded: false } },
    );

    expect(sortedSetValues(result.current.seenNotificationIds)).toEqual(['alpha', 'old']);
    expect(storedSeenState()).toEqual({ 'player:p1': ['alpha', 'old'] });

    rerender({ currentIds: ['alpha', 'beta'], isCurrentFeedLoaded: true });

    expect(sortedSetValues(result.current.seenNotificationIds)).toEqual(['alpha']);
    expect(sortedSetValues(result.current.unreadNotificationIds)).toEqual(['beta']);
    expect(storedSeenState()).toEqual({ 'player:p1': ['alpha'] });
  });

  it('keeps other selected-profile feed IDs while that feed is still loading', () => {
    writeSeenNotificationIds('player:a', ['seen-a']);
    writeSeenNotificationIds('player:b', ['seen-b']);

    const { result } = renderHook(() => useNotificationSeenState('player:b', [], { isCurrentFeedLoaded: false }));

    expect(sortedSetValues(result.current.seenNotificationIds)).toEqual(['seen-b']);
    expect(storedSeenState()).toEqual({
      'player:a': ['seen-a'],
      'player:b': ['seen-b'],
    });
  });

  it('still prunes seen IDs when a loaded feed is empty', () => {
    writeSeenNotificationIds('player:p1', ['alpha']);

    const { result } = renderHook(() => useNotificationSeenState('player:p1', [], { isCurrentFeedLoaded: true }));

    expect(sortedSetValues(result.current.seenNotificationIds)).toEqual([]);
    expect(localStorage.getItem(NOTIFICATION_SEEN_STORAGE_KEY)).toBeNull();
  });
});
