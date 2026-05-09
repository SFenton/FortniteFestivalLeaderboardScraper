import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { SelectedProfile } from '../../hooks/data/useSelectedProfile';

export const NOTIFICATION_SEEN_STORAGE_KEY = 'fst:notificationSeen:v1';
export const NOTIFICATION_MOCK_FEED_KEY = 'mock';

type NotificationSeenStore = Record<string, string[]>;
type NotificationStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

function getStorage(): NotificationStorage | null {
  if (typeof localStorage === 'undefined') return null;
  return localStorage;
}

function normalizeNotificationId(value: unknown): string | null {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function normalizeNotificationIds(values: Iterable<unknown>): string[] {
  const ids = new Set<string>();
  for (const value of values) {
    const id = normalizeNotificationId(value);
    if (id) ids.add(id);
  }
  return Array.from(ids).sort();
}

function normalizeNotificationIdSet(values: Iterable<unknown>): Set<string> {
  return new Set(normalizeNotificationIds(values));
}

function readStore(storage: NotificationStorage | null): NotificationSeenStore {
  if (!storage) return {};

  try {
    const raw = storage.getItem(NOTIFICATION_SEEN_STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};

    const store: NotificationSeenStore = {};
    for (const [feedKey, ids] of Object.entries(parsed as Record<string, unknown>)) {
      if (!Array.isArray(ids)) continue;
      const normalizedFeedKey = normalizeNotificationId(feedKey);
      if (!normalizedFeedKey) continue;
      const normalizedIds = normalizeNotificationIds(ids);
      if (normalizedIds.length > 0) store[normalizedFeedKey] = normalizedIds;
    }
    return store;
  } catch {
    return {};
  }
}

function writeStore(storage: NotificationStorage | null, store: NotificationSeenStore): void {
  if (!storage) return;

  try {
    if (Object.keys(store).length === 0) {
      storage.removeItem(NOTIFICATION_SEEN_STORAGE_KEY);
      return;
    }
    storage.setItem(NOTIFICATION_SEEN_STORAGE_KEY, JSON.stringify(store));
  } catch {
    // Best-effort local state only.
  }
}

function replaceSeenIds(feedKey: string, ids: Iterable<string>, storage: NotificationStorage | null): Set<string> {
  const normalizedFeedKey = normalizeNotificationId(feedKey) ?? NOTIFICATION_MOCK_FEED_KEY;
  const normalizedIds = normalizeNotificationIds(ids);
  const store = readStore(storage);

  if (normalizedIds.length > 0) store[normalizedFeedKey] = normalizedIds;
  else delete store[normalizedFeedKey];

  writeStore(storage, store);
  return new Set(normalizedIds);
}

export function notificationFeedKeyForProfile(profile: SelectedProfile | null): string {
  if (profile?.type === 'player') return `player:${profile.accountId}`;
  if (profile?.type === 'band') return `band:${profile.bandId}`;
  return NOTIFICATION_MOCK_FEED_KEY;
}

export function readSeenNotificationIds(feedKey: string, storage: NotificationStorage | null = getStorage()): Set<string> {
  const normalizedFeedKey = normalizeNotificationId(feedKey) ?? NOTIFICATION_MOCK_FEED_KEY;
  return new Set(readStore(storage)[normalizedFeedKey] ?? []);
}

export function writeSeenNotificationIds(
  feedKey: string,
  seenNotificationIds: Iterable<string>,
  storage: NotificationStorage | null = getStorage(),
): Set<string> {
  return replaceSeenIds(feedKey, seenNotificationIds, storage);
}

export function pruneSeenNotificationIds(
  feedKey: string,
  currentNotificationIds: Iterable<string>,
  storage: NotificationStorage | null = getStorage(),
): Set<string> {
  const currentIds = normalizeNotificationIdSet(currentNotificationIds);
  const seenIds = readSeenNotificationIds(feedKey, storage);
  const prunedIds = Array.from(seenIds).filter(id => currentIds.has(id));
  return replaceSeenIds(feedKey, prunedIds, storage);
}

export function addSeenNotificationIds(
  feedKey: string,
  seenNotificationIds: Iterable<string>,
  currentNotificationIds: Iterable<string>,
  storage: NotificationStorage | null = getStorage(),
): Set<string> {
  const currentIds = normalizeNotificationIdSet(currentNotificationIds);
  const nextSeenIds = new Set(
    Array.from(readSeenNotificationIds(feedKey, storage)).filter(id => currentIds.has(id)),
  );

  for (const id of normalizeNotificationIds(seenNotificationIds)) {
    if (currentIds.has(id)) nextSeenIds.add(id);
  }

  return replaceSeenIds(feedKey, nextSeenIds, storage);
}

export function deriveUnreadNotificationIds(currentNotificationIds: Iterable<string>, seenNotificationIds: Iterable<string>): Set<string> {
  const seenIds = normalizeNotificationIdSet(seenNotificationIds);
  return new Set(normalizeNotificationIds(currentNotificationIds).filter(id => !seenIds.has(id)));
}

export function countUnreadNotifications(currentNotificationIds: Iterable<string>, seenNotificationIds: Iterable<string>): number {
  return deriveUnreadNotificationIds(currentNotificationIds, seenNotificationIds).size;
}

export function useNotificationSeenState(feedKey: string, currentNotificationIds: readonly string[]) {
  const currentIds = useMemo(() => normalizeNotificationIds(currentNotificationIds), [currentNotificationIds]);
  const currentIdsSignature = currentIds.join('\n');
  const currentIdsRef = useRef(currentIds);
  const [seenNotificationIds, setSeenNotificationIds] = useState(() => pruneSeenNotificationIds(feedKey, currentIds));

  useEffect(() => {
    currentIdsRef.current = currentIds;
  }, [currentIds, currentIdsSignature]);

  useEffect(() => {
    setSeenNotificationIds(pruneSeenNotificationIds(feedKey, currentIds));
  }, [currentIds, currentIdsSignature, feedKey]);

  const markNotificationsSeen = useCallback((notificationIds: Iterable<string>) => {
    setSeenNotificationIds(addSeenNotificationIds(feedKey, notificationIds, currentIdsRef.current));
  }, [feedKey]);

  const unreadNotificationIds = useMemo(
    () => deriveUnreadNotificationIds(currentIds, seenNotificationIds),
    [currentIds, currentIdsSignature, seenNotificationIds],
  );

  return useMemo(() => ({
    seenNotificationIds,
    unreadNotificationIds,
    unreadCount: unreadNotificationIds.size,
    markNotificationsSeen,
  }), [markNotificationsSeen, seenNotificationIds, unreadNotificationIds]);
}