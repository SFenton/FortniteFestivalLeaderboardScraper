import { act, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  NOTIFICATION_FRESHNESS_STORAGE_KEY,
  readNotificationFreshnessState,
  updateNotificationFreshnessState,
  useNotificationFreshnessState,
} from '../../../src/components/notifications/notificationFreshnessState';
import { NOTIFICATION_FEED_RETENTION_MS, touchNotificationFeed } from '../../../src/components/notifications/notificationSeenState';

function sortedSetValues(values: ReadonlySet<string>) {
  return Array.from(values).sort();
}

function storedFreshnessState() {
  return JSON.parse(localStorage.getItem(NOTIFICATION_FRESHNESS_STORAGE_KEY) ?? '{}') as Record<string, unknown>;
}

describe('notificationFreshnessState', () => {
  beforeEach(() => {
    vi.useRealTimers();
    localStorage.clear();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('marks first-seen notifications as the current New cohort', () => {
    const snapshot = updateNotificationFreshnessState('player:p1', ['beta', 'alpha', 'alpha'], 10);

    expect(sortedSetValues(snapshot.knownNotificationIds)).toEqual(['alpha', 'beta']);
    expect(sortedSetValues(snapshot.newNotificationIds)).toEqual(['alpha', 'beta']);
    expect(snapshot.sourceVersion).toBe('10');
  });

  it('moves the previous New cohort to Older only when unknown notifications arrive', () => {
    updateNotificationFreshnessState('player:p1', ['alpha', 'beta'], 10);

    const unchangedSnapshot = updateNotificationFreshnessState('player:p1', ['alpha', 'beta'], 10);
    expect(sortedSetValues(unchangedSnapshot.newNotificationIds)).toEqual(['alpha', 'beta']);

    const newSnapshot = updateNotificationFreshnessState('player:p1', ['alpha', 'beta', 'gamma'], 10);

    expect(sortedSetValues(newSnapshot.knownNotificationIds)).toEqual(['alpha', 'beta', 'gamma']);
    expect(sortedSetValues(newSnapshot.newNotificationIds)).toEqual(['gamma']);
  });

  it('retires New when the source advances with no unknown notifications', () => {
    updateNotificationFreshnessState('player:p1', ['alpha', 'beta'], 10);

    const snapshot = updateNotificationFreshnessState('player:p1', ['alpha', 'beta'], 11);

    expect(sortedSetValues(snapshot.newNotificationIds)).toEqual([]);
    expect(snapshot.sourceVersion).toBe('11');
    expect(sortedSetValues(readNotificationFreshnessState('player:p1').knownNotificationIds)).toEqual(['alpha', 'beta']);
  });

  it('keeps New stable when no source watermark is available', () => {
    updateNotificationFreshnessState('player:p1', ['alpha'], undefined);

    const snapshot = updateNotificationFreshnessState('player:p1', ['alpha'], undefined);

    expect(sortedSetValues(snapshot.newNotificationIds)).toEqual(['alpha']);
    expect(snapshot.sourceVersion).toBeNull();
  });

  it('normalizes corrupt storage and fallback feed keys', () => {
    localStorage.setItem(NOTIFICATION_FRESHNESS_STORAGE_KEY, 'not-json');
    expect(sortedSetValues(readNotificationFreshnessState('player:p1').newNotificationIds)).toEqual([]);

    localStorage.setItem(NOTIFICATION_FRESHNESS_STORAGE_KEY, JSON.stringify({
      valid: { knownNotificationIds: ['alpha', '', 42], newNotificationIds: ['alpha'], sourceVersion: 12 },
      ignored: 'alpha',
      ' ': { knownNotificationIds: ['bad-feed'] },
    }));

    expect(sortedSetValues(readNotificationFreshnessState('valid').knownNotificationIds)).toEqual(['alpha']);
    expect(readNotificationFreshnessState('valid').sourceVersion).toBe('12');
  });

  it('supports unavailable storage without throwing', () => {
    const snapshot = updateNotificationFreshnessState('player:p1', ['alpha'], 10, null);

    expect(sortedSetValues(snapshot.newNotificationIds)).toEqual(['alpha']);
    expect(sortedSetValues(readNotificationFreshnessState('player:p1', null).newNotificationIds)).toEqual([]);
  });

  it('prunes abandoned feed cohorts after retention', () => {
    const now = new Date('2026-05-09T16:00:00Z');
    vi.useFakeTimers();
    vi.setSystemTime(now);

    updateNotificationFreshnessState('player:old', ['old'], 9);
    updateNotificationFreshnessState('player:current', ['current'], 10);
    touchNotificationFeed('player:old', localStorage, now.getTime() - NOTIFICATION_FEED_RETENTION_MS - 1);

    updateNotificationFreshnessState('player:current', ['current', 'next'], 10);

    expect(storedFreshnessState()).toEqual({
      'player:current': {
        knownNotificationIds: ['current', 'next'],
        newNotificationIds: ['next'],
        sourceVersion: '10',
      },
    });
  });

  it('updates cohort state from the hook when notifications and source change', () => {
    const { result, rerender } = renderHook(
      ({ currentIds, sourceVersion }) => useNotificationFreshnessState('player:p1', currentIds, sourceVersion),
      { initialProps: { currentIds: ['alpha'], sourceVersion: 10 as number | null } },
    );

    expect(sortedSetValues(result.current.newNotificationIds)).toEqual(['alpha']);

    rerender({ currentIds: ['alpha', 'beta'], sourceVersion: 10 });
    expect(sortedSetValues(result.current.newNotificationIds)).toEqual(['beta']);

    act(() => rerender({ currentIds: ['alpha', 'beta'], sourceVersion: 11 }));
    expect(sortedSetValues(result.current.newNotificationIds)).toEqual([]);
    expect(storedFreshnessState()).toEqual({
      'player:p1': {
        knownNotificationIds: ['alpha', 'beta'],
        newNotificationIds: [],
        sourceVersion: '11',
      },
    });
  });
});