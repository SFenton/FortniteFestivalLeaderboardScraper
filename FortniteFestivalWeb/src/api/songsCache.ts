import type { ServerSong, SongsResponse } from '@festival/core/api/serverTypes';

export const SONGS_CACHE_KEY = 'fst_songs_cache';
export const SONGS_CACHE_VERSION = 3;
export const PUBLIC_CATALOG_CACHE_SCOPE = 'public';

export type SongsCacheEntry = {
  data: SongsResponse;
  etag: string | null;
  scope: typeof PUBLIC_CATALOG_CACHE_SCOPE;
  version: typeof SONGS_CACHE_VERSION;
};

type LegacySongsCache = {
  data: SongsResponse;
  etag: string | null;
  v: 2;
};

let memoizedRaw: string | null | undefined;
let memoizedEntry: SongsCacheEntry | null = null;

function getStorage(): Storage | null {
  return typeof localStorage === 'undefined' ? null : localStorage;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function hasValidOptionalString(record: Record<string, unknown>, key: string): boolean {
  return record[key] === undefined || typeof record[key] === 'string';
}

function hasValidOptionalNumber(record: Record<string, unknown>, key: string): boolean {
  return record[key] === undefined || isFiniteNumber(record[key]);
}

function isNumberRecord(value: unknown): boolean {
  return isRecord(value) && Object.values(value).every(isFiniteNumber);
}

function isPopulationTierData(value: unknown): boolean {
  if (!isRecord(value) || !isFiniteNumber(value.baseCount) || !Array.isArray(value.tiers)) {
    return false;
  }
  return value.tiers.every(tier => (
    isRecord(tier)
    && isFiniteNumber(tier.leeway)
    && isFiniteNumber(tier.total)
  ));
}

function isPopulationTierRecord(value: unknown): boolean {
  return isRecord(value) && Object.values(value).every(isPopulationTierData);
}

function isServerSong(value: unknown): value is ServerSong {
  if (!isRecord(value)) return false;
  if (
    typeof value.songId !== 'string'
    || typeof value.title !== 'string'
    || typeof value.artist !== 'string'
  ) {
    return false;
  }
  if (
    !hasValidOptionalString(value, 'album')
    || !hasValidOptionalString(value, 'sig')
    || !hasValidOptionalString(value, 'albumArt')
    || !hasValidOptionalNumber(value, 'year')
    || !hasValidOptionalNumber(value, 'tempo')
    || !hasValidOptionalNumber(value, 'durationSeconds')
  ) {
    return false;
  }
  if (
    value.genres !== undefined
    && (!Array.isArray(value.genres) || !value.genres.every(genre => typeof genre === 'string'))
  ) {
    return false;
  }
  if (value.difficulty !== undefined && !isNumberRecord(value.difficulty)) return false;
  if (value.maxScores !== undefined && !isNumberRecord(value.maxScores)) return false;
  return value.populationTiers === undefined
    || value.populationTiers === null
    || isPopulationTierRecord(value.populationTiers);
}

export function isSongsResponse(value: unknown): value is SongsResponse {
  if (!isRecord(value) || !Array.isArray(value.songs) || !value.songs.every(isServerSong)) {
    return false;
  }
  if (!isFiniteNumber(value.count) || value.count < 0) return false;
  return value.currentSeason === undefined || isFiniteNumber(value.currentSeason);
}

function normalizeCache(value: unknown): SongsCacheEntry | null {
  if (!isRecord(value) || !isSongsResponse(value.data)) return null;
  if (value.etag !== null && typeof value.etag !== 'string') return null;

  if (
    value.version === SONGS_CACHE_VERSION
    && value.scope === PUBLIC_CATALOG_CACHE_SCOPE
  ) {
    return value as SongsCacheEntry;
  }

  if (value.v === 2) {
    const legacy = value as LegacySongsCache;
    return {
      data: legacy.data,
      etag: legacy.etag,
      scope: PUBLIC_CATALOG_CACHE_SCOPE,
      version: SONGS_CACHE_VERSION,
    };
  }

  return null;
}

function setMemo(raw: string | null, entry: SongsCacheEntry | null): void {
  memoizedRaw = raw;
  memoizedEntry = entry;
}

function removeStoredCache(storage: Storage): void {
  try {
    storage.removeItem(SONGS_CACHE_KEY);
  } catch {
    // Storage can be unavailable; the in-memory memo is still cleared.
  }
  setMemo(null, null);
}

export function readSongsCache(): SongsCacheEntry | null {
  const storage = getStorage();
  if (!storage) return null;

  try {
    const raw = storage.getItem(SONGS_CACHE_KEY);
    if (raw === memoizedRaw) return memoizedEntry;
    if (!raw) {
      setMemo(null, null);
      return null;
    }

    const entry = normalizeCache(JSON.parse(raw));
    if (!entry) {
      removeStoredCache(storage);
      return null;
    }

    const serialized = JSON.stringify(entry);
    if (serialized !== raw) storage.setItem(SONGS_CACHE_KEY, serialized);
    setMemo(serialized, entry);
    return entry;
  } catch {
    removeStoredCache(storage);
    return null;
  }
}

export function writeSongsCache(data: SongsResponse, etag: string | null): void {
  const storage = getStorage();
  if (!storage || !isSongsResponse(data)) return;

  const entry: SongsCacheEntry = {
    data,
    etag,
    scope: PUBLIC_CATALOG_CACHE_SCOPE,
    version: SONGS_CACHE_VERSION,
  };
  try {
    const raw = JSON.stringify(entry);
    storage.setItem(SONGS_CACHE_KEY, raw);
    setMemo(raw, entry);
  } catch {
    // Quota failures leave the current in-memory query result untouched.
  }
}

export function clearSongsCache(): void {
  const storage = getStorage();
  if (storage) removeStoredCache(storage);
  else setMemo(null, null);
}
