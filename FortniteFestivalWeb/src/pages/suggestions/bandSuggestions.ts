import { ScoreTracker } from '@festival/core/models';
import type { LeaderboardData, Song as CoreSong } from '@festival/core/models';
import type { BandSongPerformance, BandType, ServerSong } from '@festival/core/api/serverTypes';
import { serverSongToCore } from '../../utils/suggestionAdapter';

const BAND_TRACKER_KEY = 'guitar';

export type BandSuggestionSource = {
  songs: CoreSong[];
  scoresIndex: Record<string, LeaderboardData>;
};

export type BandSuggestionInput = {
  songs: ServerSong[];
  performances: BandSongPerformance[];
  bandType: BandType;
  comboId?: string | null;
  currentSeason?: number;
};

export function buildBandSuggestionSource({
  songs,
  performances,
}: BandSuggestionInput): BandSuggestionSource {
  const songById = new Map(songs.map(song => [song.songId, song]));
  const knownPerformances = performances.filter(performance => songById.has(performance.songId));
  return {
    songs: songs.map(serverSongToCore),
    scoresIndex: buildBandScoresIndex(knownPerformances, songById),
  };
}

function buildBandScoresIndex(
  performances: BandSongPerformance[],
  songById: Map<string, ServerSong>,
): Record<string, LeaderboardData> {
  const index: Record<string, LeaderboardData> = {};

  for (const performance of performances) {
    const song = songById.get(performance.songId);
    if (!song) continue;
    const board: LeaderboardData = {
      songId: performance.songId,
      title: song.title,
      artist: song.artist,
    };
    (board as Record<string, unknown>)[BAND_TRACKER_KEY] = buildTracker(performance);
    index[performance.songId] = board;
  }

  return index;
}

function buildTracker(performance: BandSongPerformance): ScoreTracker {
  const tracker = new ScoreTracker();
  tracker.initialized = true;
  tracker.maxScore = performance.score;
  tracker.numStars = performance.stars ?? 0;
  tracker.isFullCombo = performance.isFullCombo ?? false;
  tracker.percentHit = accuracyRaw(performance.accuracy);
  tracker.rank = performance.rank;
  tracker.totalEntries = performance.totalEntries;
  tracker.seasonAchieved = performance.season ?? 0;
  if (performance.percentile > 0) {
    tracker.rawPercentile = performance.percentile / 100;
  } else if (performance.rank > 0 && performance.totalEntries > 0) {
    tracker.rawPercentile = performance.rank / performance.totalEntries;
  }
  tracker.refreshDerived();
  return tracker;
}

function accuracyRaw(value: number | null | undefined): number {
  if (value == null || !Number.isFinite(value) || value <= 0) return 0;
  if (value <= 1) return value * 1_000_000;
  if (value <= 100) return value * 10_000;
  return value;
}
