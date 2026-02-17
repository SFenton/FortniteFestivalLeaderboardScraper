import type {FestivalPersistence} from '@festival/core';
import {KeyValueFestivalPersistence} from './keyValueFestivalPersistence';
import {AsyncStorageKeyValueStore} from './asyncStorageKeyValueStore';

export const DEFAULT_ASYNC_STORAGE_KEYS = {
  songsKey: 'fnfestival:songs',
  scoresKey: 'fnfestival:scores',
} as const;

export function createAsyncStorageFestivalPersistence(): FestivalPersistence {
  return new KeyValueFestivalPersistence(new AsyncStorageKeyValueStore(), {
    songsKey: DEFAULT_ASYNC_STORAGE_KEYS.songsKey,
    scoresKey: DEFAULT_ASYNC_STORAGE_KEYS.scoresKey,
  });
}
