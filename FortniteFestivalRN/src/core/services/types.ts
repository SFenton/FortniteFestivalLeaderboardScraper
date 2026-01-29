import type {LeaderboardData, Song} from '../models';
import type {Settings} from '../settings';

export type HttpResponse = {
  ok: boolean;
  status: number;
  text: string;
};

export interface HttpClient {
  getText(url: string, opts?: {headers?: Record<string, string>; signal?: AbortSignal}): Promise<HttpResponse>;
  postForm(
    url: string,
    form: Record<string, string>,
    opts?: {headers?: Record<string, string>; signal?: AbortSignal},
  ): Promise<HttpResponse>;
  getBytes(
    url: string,
    opts?: {headers?: Record<string, string>; signal?: AbortSignal},
  ): Promise<{ok: boolean; status: number; bytes: Uint8Array}>;
}

export interface ImageCache {
  ensureCached(song: Song, opts?: {signal?: AbortSignal}): Promise<string | undefined>;
}

export type FestivalServiceEvents = {
  log?: (line: string) => void;
  songAvailabilityChanged?: (songId: string) => void;
  scoreUpdated?: (board: LeaderboardData) => void;
  songProgress?: (current: number, total: number, title: string, started: boolean) => void;
  songUpdateStarted?: (songId: string) => void;
  songUpdateCompleted?: (songId: string) => void;
};

export type Instrumentation = {
  improved: number;
  empty: number;
  errors: number;
  requests: number;
  bytes: number;
  elapsedSec: number;
};

export type FetchScoresParams = {
  exchangeCode: string;
  degreeOfParallelism: number;
  filteredSongIds?: string[];
  settings?: Settings;
  signal?: AbortSignal;
};
