/**
 * Converts FSTService API types to the core-package Song / LeaderboardData
 * shapes expected by SuggestionGenerator.
 */
import { ScoreTracker } from '@festival/core/models';
import type { Song as CoreSong, LeaderboardData } from '@festival/core/models';
import type { InstrumentKey } from '@festival/core/instruments';
import type { ServerSong, PlayerScore, ServerInstrumentKey } from '@festival/core/api/serverTypes';

const SERVER_TO_CORE_INSTRUMENT: Record<ServerInstrumentKey, InstrumentKey> = {
  Solo_Guitar: 'guitar',
  Solo_Bass: 'bass',
  Solo_Drums: 'drums',
  Solo_Vocals: 'vocals',
  Solo_PeripheralGuitar: 'pro_guitar',
  Solo_PeripheralBass: 'pro_bass',
};

export function serverSongToCore(s: ServerSong): CoreSong {
  return {
    _title: s.title,
    track: {
      su: s.songId,
      tt: s.title,
      an: s.artist,
      au: s.albumArt,
      ry: s.year,
      mt: s.tempo,
      in: s.difficulty
        ? {
            gr: s.difficulty.guitar,
            ba: s.difficulty.bass,
            ds: s.difficulty.drums,
            vl: s.difficulty.vocals,
            pg: s.difficulty.proGuitar,
            pb: s.difficulty.proBass,
          }
        : undefined,
    },
  };
}

function buildTracker(ps: PlayerScore): ScoreTracker {
  const t = new ScoreTracker();
  t.initialized = true;
  t.maxScore = ps.score;
  t.numStars = ps.stars ?? 0;
  t.isFullCombo = ps.isFullCombo ?? false;
  t.percentHit = ps.accuracy ?? 0; // already in ×10 000 form from API
  t.rank = ps.rank;
  t.totalEntries = ps.totalEntries ?? 0;
  t.seasonAchieved = ps.season ?? 0;

  // percentile from API is 0-100 "top X%"; convert to raw fraction
  if (ps.percentile != null && ps.percentile > 0) {
    t.rawPercentile = ps.percentile / 100;
  } else if (t.rank > 0 && t.totalEntries > 0) {
    // Fallback: compute from rank/totalEntries
    t.rawPercentile = t.rank / t.totalEntries;
  }

  t.refreshDerived();
  return t;
}

export function buildScoresIndex(
  scores: PlayerScore[],
): Record<string, LeaderboardData> {
  const index: Record<string, LeaderboardData> = {};

  for (const ps of scores) {
    const coreInstrument = SERVER_TO_CORE_INSTRUMENT[ps.instrument as ServerInstrumentKey];
    if (!coreInstrument) continue;

    let board = index[ps.songId];
    if (!board) {
      board = { songId: ps.songId, title: ps.songTitle, artist: ps.songArtist };
      index[ps.songId] = board;
    }

    (board as Record<string, unknown>)[coreInstrument] = buildTracker(ps);
  }

  return index;
}
