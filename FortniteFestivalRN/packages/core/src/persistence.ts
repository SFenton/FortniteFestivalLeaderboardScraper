import type {LeaderboardData, ScoreHistoryEntry, Song} from './models';

export interface FestivalPersistence {
  loadScores(): Promise<LeaderboardData[]>;
  saveScores(scores: LeaderboardData[]): Promise<void>;
  loadSongs(): Promise<Song[]>;
  saveSongs(songs: Song[]): Promise<void>;
  loadScoreHistory(songId?: string, instrument?: string): Promise<ScoreHistoryEntry[]>;
  saveScoreHistory(entries: ScoreHistoryEntry[]): Promise<void>;
  /** Delete all scores and score history from persistence (keeps songs + images). */
  clearScoresAndHistory(): Promise<void>;
}

export class InMemoryFestivalPersistence implements FestivalPersistence {
  private songs: Song[] = [];
  private scores: LeaderboardData[] = [];
  private scoreHistory: ScoreHistoryEntry[] = [];

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
  async loadScoreHistory(songId?: string, instrument?: string): Promise<ScoreHistoryEntry[]> {
    let entries = JSON.parse(JSON.stringify(this.scoreHistory)) as ScoreHistoryEntry[];
    if (songId) entries = entries.filter(e => e.songId === songId);
    if (instrument) entries = entries.filter(e => e.instrument === instrument);
    return entries;
  }
  async saveScoreHistory(entries: ScoreHistoryEntry[]): Promise<void> {
    this.scoreHistory = JSON.parse(JSON.stringify(entries)) as ScoreHistoryEntry[];
  }
  async clearScoresAndHistory(): Promise<void> {
    this.scores = [];
    this.scoreHistory = [];
  }
}
