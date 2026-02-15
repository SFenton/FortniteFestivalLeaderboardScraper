import type {LeaderboardData, Song} from '../../models';
import {savePretty} from '../../io/jsonSerializer';
import type {FestivalPersistence} from '../../persistence';
import type {FileStore} from './fileStore.types';

export class FileJsonFestivalPersistence implements FestivalPersistence {
  constructor(
    private readonly store: FileStore,
    private readonly paths: {scoresPath: string; songsPath?: string},
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
}
