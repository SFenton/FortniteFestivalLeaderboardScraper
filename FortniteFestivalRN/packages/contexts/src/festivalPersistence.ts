import {Platform} from 'react-native';
import type {FestivalPersistence} from '@festival/core';
import {InMemoryFestivalPersistence} from '@festival/core';

declare const process:
  | {
      env?: Record<string, string | undefined>;
    }
  | undefined;

export type PersistenceKind = 'windows-key-value' | 'mobile-sqlite' | 'not-configured';

export function getPersistenceKind(): PersistenceKind {
  if (Platform.OS === 'windows') return 'windows-key-value';
  if (Platform.OS === 'ios' || Platform.OS === 'android') return 'mobile-sqlite';
  return 'not-configured';
}

let cachedPersistence: FestivalPersistence | null = null;
let cachedKind: PersistenceKind | null = null;

type GlobalPersistenceCache = {
  __fnfestivalPersistence?: FestivalPersistence;
  __fnfestivalPersistenceKind?: PersistenceKind;
};

function getGlobalCache(): GlobalPersistenceCache {
  // In RN, `globalThis` is available and survives Fast Refresh.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  return globalThis as any as GlobalPersistenceCache;
}

export function createFestivalPersistence(): FestivalPersistence {
  // Jest runs in a JS-only environment; native DB modules will crash.
  const isJest = typeof process !== 'undefined' && !!process?.env?.JEST_WORKER_ID;
  if (isJest) return new InMemoryFestivalPersistence();

  const kind = getPersistenceKind();

  const g = getGlobalCache();
  if (g.__fnfestivalPersistence && g.__fnfestivalPersistenceKind === kind) {
    return g.__fnfestivalPersistence;
  }

  if (cachedPersistence && cachedKind === kind) return cachedPersistence;

  if (Platform.OS === 'windows') {
    // Windows uses AsyncStorage-backed KeyValue persistence.
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const {KeyValueFestivalPersistence} = require('./adapters/storage/keyValueFestivalPersistence') as typeof import('./adapters/storage/keyValueFestivalPersistence');
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const {AsyncStorageKeyValueStore} = require('./adapters/storage/asyncStorageKeyValueStore') as typeof import('./adapters/storage/asyncStorageKeyValueStore');

    cachedPersistence = new KeyValueFestivalPersistence(new AsyncStorageKeyValueStore(), {
      songsKey: 'fnfestival:songs',
      scoresKey: 'fnfestival:scores',
      scoreHistoryKey: 'fnfestival:scoreHistory',
    });
    cachedKind = kind;
    g.__fnfestivalPersistence = cachedPersistence;
    g.__fnfestivalPersistenceKind = kind;
    return cachedPersistence;
  }

  if (Platform.OS === 'ios' || Platform.OS === 'android') {
    try {
      console.log('[festivalPersistence] Creating SQLite persistence for mobile...');
      // eslint-disable-next-line @typescript-eslint/no-var-requires
      const {openDatabase} = require('./adapters/sqlite/openDatabase') as typeof import('./adapters/sqlite/openDatabase');
      // eslint-disable-next-line @typescript-eslint/no-var-requires
      const {SqliteFestivalPersistence} = require('./adapters/sqlite/sqliteFestivalPersistence') as typeof import('./adapters/sqlite/sqliteFestivalPersistence');

      console.log('[festivalPersistence] Opening SQLite database...');
      const db = openDatabase({name: 'fnfestival.sqlite'});
      console.log('[festivalPersistence] SQLite database opened successfully');
      cachedPersistence = new SqliteFestivalPersistence(db);
      cachedKind = kind;
      g.__fnfestivalPersistence = cachedPersistence;
      g.__fnfestivalPersistenceKind = kind;
      return cachedPersistence;
    } catch (error) {
      console.error('[festivalPersistence] Failed to create SQLite persistence:', error);
      console.log('[festivalPersistence] Falling back to InMemoryFestivalPersistence');
      return new InMemoryFestivalPersistence();
    }
  }

  return new InMemoryFestivalPersistence();
}
