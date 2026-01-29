import type {FestivalPersistence} from '../../core/persistence';
import type {LeaderboardData, Song} from '../../core/models';
import {parseJson, savePretty} from '../../core/io/jsonSerializer';
import type {KeyValueStore} from './keyValueStore.types';

export type KeyValueFestivalPersistenceKeys = {
  songsKey: string;
  scoresKey: string;
};

function safeArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? (value as T[]) : [];
}

export class KeyValueFestivalPersistence implements FestivalPersistence {
  private readonly store: KeyValueStore;
  private readonly keys: KeyValueFestivalPersistenceKeys;

  constructor(store: KeyValueStore, keys: KeyValueFestivalPersistenceKeys) {
    this.store = store;
    this.keys = keys;
  }

  async loadScores(): Promise<LeaderboardData[]> {
    try {
      const raw = await this.store.getItem(this.keys.scoresKey);
      if (!raw) return [];
      const parsed = parseJson(raw);
      return safeArray<LeaderboardData>(parsed);
    } catch {
      return [];
    }
  }

  async saveScores(scores: LeaderboardData[]): Promise<void> {
    try {
      const json = savePretty(scores);
      if (!json) return;
      await this.store.setItem(this.keys.scoresKey, json);
    } catch {
      // swallow
    }
  }

  async loadSongs(): Promise<Song[]> {
    try {
      const raw = await this.store.getItem(this.keys.songsKey);
      if (!raw) return [];
      const parsed = parseJson(raw);
      return safeArray<Song>(parsed);
    } catch {
      return [];
    }
  }

  async saveSongs(songs: Song[]): Promise<void> {
    try {
      const json = savePretty(songs);
      if (!json) return;
      await this.store.setItem(this.keys.songsKey, json);
    } catch {
      // swallow
    }
  }
}
