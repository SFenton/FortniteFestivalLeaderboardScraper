/**
 * SQLite database factory for iOS / Android.
 *
 * Opens databases via react-native-nitro-sqlite.
 * Windows does NOT use SQLite — it uses KeyValue (AsyncStorage) persistence
 * and receives data from the server via a JSON sync endpoint.
 *
 * All consumers should use this function instead of importing the native
 * modules directly. The returned object conforms to `SqliteDatabase`.
 */
import type {SqliteDatabase} from './sqliteDb.types';

export type OpenDatabaseOptions = {
  /** Database filename (e.g. 'fnfestival.sqlite'). */
  name: string;
  /** Optional directory path. When provided, the DB is opened from this location. */
  location?: string;
};

/**
 * Open (or return a cached) SQLite database via nitro-sqlite (iOS/Android only).
 */
export function openDatabase(opts: OpenDatabaseOptions): SqliteDatabase {
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const {openNitroSqliteDatabase} = require('./nitroSqliteDatabase') as typeof import('./nitroSqliteDatabase');
  return openNitroSqliteDatabase(opts);
}
