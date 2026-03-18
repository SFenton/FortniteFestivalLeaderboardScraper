import type {LeaderboardData, ScoreHistoryEntry, Song} from '@festival/core';
import {savePretty} from '../../io/jsonSerializer';
import type {FestivalPersistence} from '@festival/core';
import type {FileStore} from './fileStore.types';

export class FileJsonFestivalPersistence implements FestivalPersistence {
  constructor(
    private readonly store: FileStore,
    private readonly paths: {
      scoresPath: string;
      songsPath?: string;
      scoreHistoryPath?: string;
    },
  ) {}

  async loadScores(): Promise<LeaderboardData[]> {
    try {
      const exists = await this.store.exists(this.paths.scoresPath);
      if (!exists) return [];
      const json = await this.store.readText(this.paths.scoresPath);
      const list = (JSON.parse(json) as LeaderboardData[]) ?? [];
      return Array.isArray(list) ? list : [];
    } catch {
      return [];
    }
  }

  async saveScores(scores: LeaderboardData[]): Promise<void> {
    try {
      const json = savePretty(scores);
      if (!json) return;
      await this.store.writeText(this.paths.scoresPath, json);
    } catch {
      // swallow
    }
  }

  async loadSongs(): Promise<Song[]> {
    if (!this.paths.songsPath) return [];
    try {
      const exists = await this.store.exists(this.paths.songsPath);
      if (!exists) return [];
      const json = await this.store.readText(this.paths.songsPath);
      const list = (JSON.parse(json) as Song[]) ?? [];
      return Array.isArray(list) ? list : [];
    } catch {
      return [];
    }
  }

  async saveSongs(songs: Song[]): Promise<void> {
    if (!this.paths.songsPath) return;
    try {
      const json = savePretty(songs);
      if (!json) return;
      await this.store.writeText(this.paths.songsPath, json);
    } catch {
      // swallow
    }
  }

  async loadScoreHistory(
    songId?: string,
    instrument?: string,
  ): Promise<ScoreHistoryEntry[]> {
    if (!this.paths.scoreHistoryPath) return [];
    try {
      const exists = await this.store.exists(this.paths.scoreHistoryPath);
      if (!exists) return [];
      const json = await this.store.readText(this.paths.scoreHistoryPath);
      let list = (JSON.parse(json) as ScoreHistoryEntry[]) ?? [];
      if (!Array.isArray(list)) return [];
      if (songId) list = list.filter(e => e.songId === songId);
      if (instrument) list = list.filter(e => e.instrument === instrument);
      return list;
    } catch {
      return [];
    }
  }

  async saveScoreHistory(entries: ScoreHistoryEntry[]): Promise<void> {
    if (!this.paths.scoreHistoryPath) return;
    try {
      const json = savePretty(entries);
      if (!json) return;
      await this.store.writeText(this.paths.scoreHistoryPath, json);
    } catch {
      // swallow
    }
  }

  async clearScoresAndHistory(): Promise<void> {
    try {
      await this.store.writeText(this.paths.scoresPath, '[]');
    } catch {
      // swallow
    }
    if (this.paths.scoreHistoryPath) {
      try {
        await this.store.writeText(this.paths.scoreHistoryPath, '[]');
      } catch {
        // swallow
      }
    }
  }
}
