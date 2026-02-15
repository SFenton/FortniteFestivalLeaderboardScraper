import type {LeaderboardData, Song} from './models';

export interface FestivalPersistence {
  loadScores(): Promise<LeaderboardData[]>;
  saveScores(scores: LeaderboardData[]): Promise<void>;
  loadSongs(): Promise<Song[]>;
  saveSongs(songs: Song[]): Promise<void>;
}

export class InMemoryFestivalPersistence implements FestivalPersistence {
  private songs: Song[] = [];
  private scores: LeaderboardData[] = [];

  async loadScores(): Promise<LeaderboardData[]> {
    return JSON.parse(JSON.stringify(this.scores)) as LeaderboardData[];
  }
  async saveScores(scores: LeaderboardData[]): Promise<void> {
    this.scores = JSON.parse(JSON.stringify(scores)) as LeaderboardData[];
  }
  async loadSongs(): Promise<Song[]> {
    return JSON.parse(JSON.stringify(this.songs)) as Song[];
  }
  async saveSongs(songs: Song[]): Promise<void> {
    this.songs = JSON.parse(JSON.stringify(songs)) as Song[];
  }
}
