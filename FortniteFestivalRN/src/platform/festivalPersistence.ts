import {Platform} from 'react-native';
import type {FestivalPersistence} from '../core/persistence';
import {InMemoryFestivalPersistence} from '../core/persistence';

export type PersistenceKind = 'windows-async-storage' | 'mobile-nitro-sqlite' | 'not-configured';

export function getPersistenceKind(): PersistenceKind {
  if (Platform.OS === 'windows') return 'windows-async-storage';
  if (Platform.OS === 'ios' || Platform.OS === 'android') return 'mobile-nitro-sqlite';
  return 'not-configured';
}

export function createFestivalPersistence(): FestivalPersistence {
  // Jest runs in a JS-only environment; native DB modules will crash.
  if (process.env.JEST_WORKER_ID) return new InMemoryFestivalPersistence();

  if (Platform.OS === 'windows') {
    // Lazy require so Jest / non-windows platforms don't try to resolve native AsyncStorage.
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const mod = require('../adapters/storage/asyncStorageFestivalPersistence') as typeof import('../adapters/storage/asyncStorageFestivalPersistence');
    return mod.createAsyncStorageFestivalPersistence();
  }

  if (Platform.OS === 'ios' || Platform.OS === 'android') {
    // Lazy require so bundlers/tests don't pull native modules unless needed.
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const {openNitroSqliteDatabase} = require('../adapters/sqlite/nitroSqliteDatabase') as typeof import('../adapters/sqlite/nitroSqliteDatabase');
    // eslint-disable-next-line @typescript-eslint/no-var-requires
    const {SqliteFestivalPersistence} = require('../adapters/sqlite/sqliteFestivalPersistence') as typeof import('../adapters/sqlite/sqliteFestivalPersistence');

    const db = openNitroSqliteDatabase({name: 'fnfestival.sqlite'});
    return new SqliteFestivalPersistence(db);
  }

  return new InMemoryFestivalPersistence();
}
