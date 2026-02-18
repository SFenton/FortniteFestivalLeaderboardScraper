import type {FestivalPersistence} from '@festival/core';
import type {LeaderboardData, ScoreHistoryEntry, Song} from '@festival/core';
import {ScoreTracker} from '@festival/core';
import {parseJson, savePretty} from '@festival/core';
import type {KeyValueStore} from './keyValueStore.types';

export type KeyValueFestivalPersistenceKeys = {
  songsKey: string;
  scoresKey: string;
  scoreHistoryKey: string;
};

function safeArray<T>(value: unknown): T[] {
  return Array.isArray(value) ? (value as T[]) : [];
}

function rehydrateTracker(value: unknown): ScoreTracker | undefined {
  if (!value || typeof value !== 'object') return undefined;
  if (value instanceof ScoreTracker) return value;

  // JSON round-trips lose prototypes; rebuild into a real ScoreTracker.
  const tracker = Object.assign(new ScoreTracker(), value as Partial<ScoreTracker>);
  tracker.refreshDerived();
  return tracker;
}

function rehydrateLeaderboardData(value: unknown): LeaderboardData {
  if (!value || typeof value !== 'object') return value as LeaderboardData;
  const ld = value as LeaderboardData;
  return {
    ...ld,
    guitar: rehydrateTracker(ld.guitar),
    drums: rehydrateTracker(ld.drums),
    bass: rehydrateTracker(ld.bass),
    vocals: rehydrateTracker(ld.vocals),
    pro_guitar: rehydrateTracker(ld.pro_guitar),
    pro_bass: rehydrateTracker(ld.pro_bass),
  };
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
      return safeArray<LeaderboardData>(parsed).map(rehydrateLeaderboardData);
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

  async loadScoreHistory(
    songId?: string,
    instrument?: string,
  ): Promise<ScoreHistoryEntry[]> {
    try {
      const raw = await this.store.getItem(this.keys.scoreHistoryKey);
      if (!raw) return [];
      const parsed = parseJson(raw);
      let entries = safeArray<ScoreHistoryEntry>(parsed);
      if (songId) entries = entries.filter(e => e.songId === songId);
      if (instrument) entries = entries.filter(e => e.instrument === instrument);
      return entries;
    } catch {
      return [];
    }
  }

  async saveScoreHistory(entries: ScoreHistoryEntry[]): Promise<void> {
    try {
      const json = savePretty(entries);
      if (!json) return;
      await this.store.setItem(this.keys.scoreHistoryKey, json);
    } catch {
      // swallow
    }
  }

  async clearScoresAndHistory(): Promise<void> {
    try {
      await this.store.setItem(this.keys.scoresKey, '[]');
      await this.store.setItem(this.keys.scoreHistoryKey, '[]');
    } catch {
      // swallow
    }
  }
}
