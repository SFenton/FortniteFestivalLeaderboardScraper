import { useEffect, useMemo, useState } from 'react';
import {
  deleteNotificationLocalStateFeeds,
  normalizeNotificationFeedKey,
  pruneStaleNotificationFeeds,
  touchNotificationFeed,
  type NotificationStorage,
} from './notificationSeenState';

export const NOTIFICATION_FRESHNESS_STORAGE_KEY = 'fst:notificationFreshness:v1';

type NotificationFreshnessFeedState = {
  knownNotificationIds: string[];
  newNotificationIds: string[];
  sourceVersion: string | null;
};
type NotificationFreshnessStore = Record<string, NotificationFreshnessFeedState>;

export type NotificationFreshnessSnapshot = {
  knownNotificationIds: Set<string>;
  newNotificationIds: Set<string>;
  sourceVersion: string | null;
};

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

function normalizeSourceVersion(value: unknown): string | null {
  if (typeof value === 'number' && Number.isFinite(value)) return String(value);
  if (typeof value === 'string') {
    const trimmed = value.trim();
    return trimmed.length > 0 ? trimmed : null;
  }
  return null;
}

function normalizeFeedKey(feedKey: string): string {
  return normalizeNotificationFeedKey(feedKey);
}

function deleteStaleFeedsFromStore(store: NotificationFreshnessStore, staleKeys: ReadonlySet<string>): boolean {
  let changed = false;
  for (const staleKey of staleKeys) {
    if (!Object.prototype.hasOwnProperty.call(store, staleKey)) continue;
    delete store[staleKey];
    changed = true;
  }
  return changed;
}

function emptyFeedState(): NotificationFreshnessFeedState {
  return { knownNotificationIds: [], newNotificationIds: [], sourceVersion: null };
}

function normalizeFeedState(value: unknown): NotificationFreshnessFeedState {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return emptyFeedState();
  const record = value as Record<string, unknown>;
  return {
    knownNotificationIds: Array.isArray(record.knownNotificationIds) ? normalizeNotificationIds(record.knownNotificationIds) : [],
    newNotificationIds: Array.isArray(record.newNotificationIds) ? normalizeNotificationIds(record.newNotificationIds) : [],
    sourceVersion: normalizeSourceVersion(record.sourceVersion),
  };
}

function readStore(storage: NotificationStorage | null): NotificationFreshnessStore {
  if (!storage) return {};

  try {
    const raw = storage.getItem(NOTIFICATION_FRESHNESS_STORAGE_KEY);
    if (!raw) return {};
    const parsed = JSON.parse(raw) as unknown;
    if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) return {};

    const store: NotificationFreshnessStore = {};
    for (const [feedKey, value] of Object.entries(parsed as Record<string, unknown>)) {
      const normalizedFeedKey = normalizeNotificationId(feedKey);
      if (!normalizedFeedKey) continue;
      const feedState = normalizeFeedState(value);
      if (feedState.knownNotificationIds.length > 0 || feedState.newNotificationIds.length > 0 || feedState.sourceVersion) {
        store[normalizedFeedKey] = feedState;
      }
    }
    return store;
  } catch {
    return {};
  }
}

function writeStore(storage: NotificationStorage | null, store: NotificationFreshnessStore): void {
  if (!storage) return;

  try {
    if (Object.keys(store).length === 0) {
      storage.removeItem(NOTIFICATION_FRESHNESS_STORAGE_KEY);
      return;
    }
    storage.setItem(NOTIFICATION_FRESHNESS_STORAGE_KEY, JSON.stringify(store));
  } catch {
    // Best-effort local state only.
  }
}

function toSnapshot(feedState: NotificationFreshnessFeedState): NotificationFreshnessSnapshot {
  return {
    knownNotificationIds: new Set(feedState.knownNotificationIds),
    newNotificationIds: new Set(feedState.newNotificationIds),
    sourceVersion: feedState.sourceVersion,
  };
}

export function updateNotificationFreshnessState(
  feedKey: string,
  currentNotificationIds: Iterable<string>,
  sourceVersion: string | number | null | undefined,
  storage: NotificationStorage | null = getStorage(),
): NotificationFreshnessSnapshot {
  const normalizedFeedKey = normalizeFeedKey(feedKey);
  const currentIds = normalizeNotificationIds(currentNotificationIds);
  const currentIdSet = new Set(currentIds);
  const nextSourceVersion = normalizeSourceVersion(sourceVersion);
  const store = readStore(storage);
  touchNotificationFeed(normalizedFeedKey, storage);
  const staleKeys = pruneStaleNotificationFeeds(normalizedFeedKey, storage);
  deleteStaleFeedsFromStore(store, staleKeys);

  if (currentIds.length === 0) {
    delete store[normalizedFeedKey];
    writeStore(storage, store);
    deleteNotificationLocalStateFeeds([normalizedFeedKey], storage);
    return toSnapshot(emptyFeedState());
  }

  const previous = store[normalizedFeedKey] ?? emptyFeedState();
  const previousKnownIds = new Set(previous.knownNotificationIds);
  const unknownIds = currentIds.filter(id => !previousKnownIds.has(id));
  const sourceAdvanced = previous.sourceVersion != null
    && nextSourceVersion != null
    && previous.sourceVersion !== nextSourceVersion;

  const nextNewIds = unknownIds.length > 0
    ? unknownIds
    : sourceAdvanced
      ? []
      : previous.newNotificationIds.filter(id => currentIdSet.has(id));
  const nextKnownIds = normalizeNotificationIds([...previous.knownNotificationIds, ...currentIds]);
  const nextFeedState = {
    knownNotificationIds: nextKnownIds,
    newNotificationIds: nextNewIds,
    sourceVersion: nextSourceVersion ?? previous.sourceVersion,
  };

  if (nextFeedState.knownNotificationIds.length > 0 || nextFeedState.newNotificationIds.length > 0 || nextFeedState.sourceVersion) {
    store[normalizedFeedKey] = nextFeedState;
  } else {
    delete store[normalizedFeedKey];
  }

  writeStore(storage, store);
  return toSnapshot(nextFeedState);
}

export function readNotificationFreshnessState(
  feedKey: string,
  storage: NotificationStorage | null = getStorage(),
): NotificationFreshnessSnapshot {
  const normalizedFeedKey = normalizeFeedKey(feedKey);
  const store = readStore(storage);
  touchNotificationFeed(normalizedFeedKey, storage);
  const staleKeys = pruneStaleNotificationFeeds(normalizedFeedKey, storage);
  if (deleteStaleFeedsFromStore(store, staleKeys)) writeStore(storage, store);
  return toSnapshot(store[normalizedFeedKey] ?? emptyFeedState());
}

export function useNotificationFreshnessState(
  feedKey: string,
  currentNotificationIds: readonly string[],
  sourceVersion: string | number | null | undefined,
  options?: { isCurrentFeedLoaded?: boolean },
): NotificationFreshnessSnapshot {
  const isCurrentFeedLoaded = options?.isCurrentFeedLoaded ?? true;
  const currentIds = useMemo(() => normalizeNotificationIds(currentNotificationIds), [currentNotificationIds]);
  const currentIdsSignature = currentIds.join('\n');
  const normalizedSourceVersion = normalizeSourceVersion(sourceVersion);
  const [freshness, setFreshness] = useState(() => (
    isCurrentFeedLoaded
      ? updateNotificationFreshnessState(feedKey, currentIds, normalizedSourceVersion)
      : readNotificationFreshnessState(feedKey)
  ));

  useEffect(() => {
    setFreshness(isCurrentFeedLoaded
      ? updateNotificationFreshnessState(feedKey, currentIds, normalizedSourceVersion)
      : readNotificationFreshnessState(feedKey));
  }, [currentIds, currentIdsSignature, feedKey, isCurrentFeedLoaded, normalizedSourceVersion]);

  return freshness;
}
