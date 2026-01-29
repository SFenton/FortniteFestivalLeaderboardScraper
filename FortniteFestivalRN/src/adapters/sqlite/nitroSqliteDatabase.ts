import type {SqliteDatabase, SqliteResultSet, SqliteRowList, SqliteTransaction} from './sqliteDb.types';

type NitroQueryResult = {
  rows?: any;
  rowsAffected?: number;
  insertId?: number;
};

type NitroDb = {
  execute: (query: string, params?: any[]) => NitroQueryResult;
  executeAsync: (query: string, params?: any[]) => Promise<NitroQueryResult>;
  transaction: (fn: (tx: NitroTx) => Promise<void> | void) => Promise<void>;
  close?: () => void;
};

type NitroTx = {
  execute: (query: string, params?: any[]) => NitroQueryResult;
  executeAsync: (query: string, params?: any[]) => Promise<NitroQueryResult>;
};

function asRowList<T>(rows: any): SqliteRowList<T> {
  // Nitro returns `rows` as an array in the docs, but we defensively handle
  // `rows.item(i)` shapes too.
  if (rows && typeof rows.length === 'number' && typeof rows.item === 'function') {
    return rows as SqliteRowList<T>;
  }

  const array = Array.isArray(rows) ? (rows as T[]) : ([] as T[]);
  return {
    length: array.length,
    item: (index: number) => array[index]!,
  };
}

function toResultSet<T>(res: NitroQueryResult): SqliteResultSet<T> {
  return {
    rows: asRowList<T>(res.rows),
    rowsAffected: res.rowsAffected,
    insertId: res.insertId,
  };
}

export type NitroSqliteOpenOptions = {
  name: string;
};

export function openNitroSqliteDatabase(opts: NitroSqliteOpenOptions): SqliteDatabase {
  // Lazy require to avoid Jest/native module crashes.
  // eslint-disable-next-line @typescript-eslint/no-var-requires
  const {open} = require('react-native-nitro-sqlite') as typeof import('react-native-nitro-sqlite');

  const db = open({name: opts.name}) as NitroDb;

  const wrapped: SqliteDatabase = {
    executeSql: async <T = any>(sql: string, params: unknown[] = []) => {
      const res = await db.executeAsync(sql, params as any[]);
      return toResultSet<T>(res);
    },

    transaction: async (fn: (tx: SqliteTransaction) => Promise<void>) => {
      await db.transaction(async (nativeTx: NitroTx) => {
        const tx: SqliteTransaction = {
          executeSql: async <T = any>(sql: string, params: unknown[] = []) => {
            const res = await nativeTx.executeAsync(sql, params as any[]);
            return toResultSet<T>(res);
          },
        };

        await fn(tx);
      });
    },
  };

  return wrapped;
}
