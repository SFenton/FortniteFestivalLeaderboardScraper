export type SqliteRowList<T = any> = {
  length: number;
  item(index: number): T;
};

export type SqliteResultSet<T = any> = {
  rows: SqliteRowList<T>;
  rowsAffected?: number;
  insertId?: number;
};

export interface SqliteTransaction {
  executeSql<T = any>(sql: string, params?: unknown[]): Promise<SqliteResultSet<T>>;
}

export interface SqliteDatabase {
  executeSql<T = any>(sql: string, params?: unknown[]): Promise<SqliteResultSet<T>>;
  transaction?(fn: (tx: SqliteTransaction) => Promise<void>): Promise<void>;
  close?(): void;
}
