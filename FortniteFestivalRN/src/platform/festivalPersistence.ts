import {Platform} from 'react-native';
import type {FestivalPersistence} from '../core/persistence';
import {InMemoryFestivalPersistence} from '../core/persistence';

declare const process:
  | {
      env?: Record<string, string | undefined>;
    }
  | undefined;

export type PersistenceKind = 'windows-async-storage' | 'mobile-nitro-sqlite' | 'not-configured';

export function getPersistenceKind(): PersistenceKind {
  if (Platform.OS === 'windows') return 'windows-async-storage';
  if (Platform.OS === 'ios' || Platform.OS === 'android') return 'mobile-nitro-sqlite';
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

  // React StrictMode may mount/unmount/remount components in dev, which can cause
  // this factory to be called multiple times. Native DB modules can throw if you
  // try to open the same database twice. Cache the persistence per-platform.
  const kind = getPersistenceKind();

  const g = getGlobalCache();
  if (g.__fnfestivalPersistence && g.__fnfestivalPersistenceKind === kind) {
    return g.__fnfestivalPersistence;
  }

  if (cachedPersistence && cachedKind === kind) return cachedPersistence;

  if (Platform.OS === 'windows') {
    // Lazy require so Jest / non-windows platforms don't try to resolve native AsyncStorage.
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const mod = require('../adapters/storage/asyncStorageFestivalPersistence') as typeof import('../adapters/storage/asyncStorageFestivalPersistence');
    cachedPersistence = mod.createAsyncStorageFestivalPersistence();
    cachedKind = kind;
    g.__fnfestivalPersistence = cachedPersistence;
    g.__fnfestivalPersistenceKind = kind;
    return cachedPersistence;
  }

  if (Platform.OS === 'ios' || Platform.OS === 'android') {
    try {
      console.log('[festivalPersistence] Creating SQLite persistence for mobile...');
      // Lazy require so bundlers/tests don't pull native modules unless needed.
      // eslint-disable-next-line @typescript-eslint/no-var-requires
      const {openNitroSqliteDatabase} = require('../adapters/sqlite/nitroSqliteDatabase') as typeof import('../adapters/sqlite/nitroSqliteDatabase');
      // eslint-disable-next-line @typescript-eslint/no-var-requires
      const {SqliteFestivalPersistence} = require('../adapters/sqlite/sqliteFestivalPersistence') as typeof import('../adapters/sqlite/sqliteFestivalPersistence');

      console.log('[festivalPersistence] Opening SQLite database...');
      const db = openNitroSqliteDatabase({name: 'fnfestival.sqlite'});
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
