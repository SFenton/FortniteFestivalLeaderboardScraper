import type {
  LeaderboardEntry,
  SongBandLeaderboardEntry,
} from '@festival/core/api/serverTypes';

export type InstrumentData = {
  entries: LeaderboardEntry[];
  loading: boolean;
  error: string | null;
  totalEntries?: number;
  localEntries?: number;
};

export type SongBandData = {
  entries: SongBandLeaderboardEntry[];
  selectedPlayerEntry?: SongBandLeaderboardEntry | null;
  selectedBandEntry?: SongBandLeaderboardEntry | null;
  loading: boolean;
  error: string | null;
  totalEntries?: number;
  localEntries?: number;
};
