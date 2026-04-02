/**
 * Converts FSTService API types to the core-package Song / LeaderboardData
 * shapes expected by SuggestionGenerator.
 */
import { ScoreTracker } from '@festival/core/models';
import type { Song as CoreSong, LeaderboardData } from '@festival/core/models';
import type { InstrumentKey } from '@festival/core/instruments';
import type { ServerSong, PlayerScore, ServerInstrumentKey, RivalSuggestionsResponse, RivalsAllResponse } from '@festival/core/api/serverTypes';
import type { RivalDataIndex, RivalInfo, RivalSongMatch } from '@festival/core/suggestions/types';

const SERVER_TO_CORE_INSTRUMENT: Record<ServerInstrumentKey, InstrumentKey> = {
  Solo_Guitar: 'guitar',
  Solo_Bass: 'bass',
  Solo_Drums: 'drums',
  Solo_Vocals: 'vocals',
  Solo_PeripheralGuitar: 'pro_guitar',
  Solo_PeripheralBass: 'pro_bass',
};

export function serverSongToCore(s: ServerSong): CoreSong {
  const maxScores: Partial<Record<InstrumentKey, number>> = {};
  if (s.maxScores) {
    for (const [serverKey, value] of Object.entries(s.maxScores) as [ServerInstrumentKey, number][]) {
      const coreKey = SERVER_TO_CORE_INSTRUMENT[serverKey];
      if (coreKey && typeof value === 'number') maxScores[coreKey] = value;
    }
  }
  const hasMaxScores = Object.keys(maxScores).length > 0;
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
    ...(hasMaxScores ? { maxScores } : undefined),
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

/**
 * Builds an indexed RivalDataIndex from the batch suggestions API response.
 * Leaderboard rivals are not included here — they come from neighborhood endpoints
 * and will be merged separately in a future phase.
 */
export function buildRivalDataIndex(response: RivalSuggestionsResponse): RivalDataIndex {
  const songRivals: RivalInfo[] = [];
  const byRival = new Map<string, RivalSongMatch[]>();
  const closestRivalBySong = new Map<string, RivalSongMatch>();

  for (const entry of response.rivals) {
    const info: RivalInfo = {
      accountId: entry.accountId,
      displayName: entry.displayName ?? 'Unknown',
      direction: entry.direction as 'above' | 'below',
      source: 'song',
      sharedSongCount: entry.sharedSongCount,
      aheadCount: entry.aheadCount,
      behindCount: entry.behindCount,
    };
    songRivals.push(info);

    const matches: RivalSongMatch[] = [];
    for (const s of entry.songs) {
      const coreInstrument = SERVER_TO_CORE_INSTRUMENT[s.instrument as ServerInstrumentKey];
      if (!coreInstrument) continue;

      const match: RivalSongMatch = {
        rival: info,
        songId: s.songId,
        instrument: coreInstrument,
        userRank: s.userRank,
        rivalRank: s.rivalRank,
        rankDelta: s.rankDelta,
        userScore: s.userScore,
        rivalScore: s.rivalScore,
      };
      matches.push(match);

      // Track closest rival per song+instrument (smallest |rankDelta|)
      const key = `${s.songId}:${coreInstrument}`;
      const existing = closestRivalBySong.get(key);
      if (!existing || Math.abs(match.rankDelta) < Math.abs(existing.rankDelta)) {
        closestRivalBySong.set(key, match);
      }
    }
    byRival.set(entry.accountId, matches);
  }

  return {
    songRivals,
    leaderboardRivals: [], // Populated when neighborhood data is merged
    allRivals: [...songRivals],
    byRival,
    closestRivalBySong,
    leaderboardRivalIndex: new Map(),
  };
}

/**
 * Builds a RivalDataIndex from the precomputed /rivals/all response (with indexed song samples).
 * Expands the integer song index back to full songIds.
 * When combo is provided, only entries from that combo are included.
 * Top N rivals per direction (default 5).
 */
export function buildRivalDataIndexFromRivalsAll(
  response: RivalsAllResponse,
  combo?: string,
  limit = 5,
): RivalDataIndex {
  const songRivals: RivalInfo[] = [];
  const byRival = new Map<string, RivalSongMatch[]>();
  const closestRivalBySong = new Map<string, RivalSongMatch>();

  // Determine which combos to include
  const comboData = combo
    ? response.combos.filter(c => c.combo === combo)
    : response.combos;

  // Collect all rivals across selected combos, dedup, take top N per direction
  const seenAbove = new Map<string, RivalInfo>();
  const seenBelow = new Map<string, RivalInfo>();

  for (const c of comboData) {
    for (const r of c.above) {
      if (!seenAbove.has(r.accountId)) {
        seenAbove.set(r.accountId, {
          accountId: r.accountId,
          displayName: r.displayName ?? 'Unknown',
          direction: 'above',
          source: 'song',
          sharedSongCount: r.sharedSongCount,
          aheadCount: r.aheadCount,
          behindCount: r.behindCount,
        });
      }
    }
    for (const r of c.below) {
      if (!seenBelow.has(r.accountId)) {
        seenBelow.set(r.accountId, {
          accountId: r.accountId,
          displayName: r.displayName ?? 'Unknown',
          direction: 'below',
          source: 'song',
          sharedSongCount: r.sharedSongCount,
          aheadCount: r.aheadCount,
          behindCount: r.behindCount,
        });
      }
    }
  }

  const topAbove = [...seenAbove.values()].slice(0, limit);
  const topBelow = [...seenBelow.values()].slice(0, limit);
  const allRivals = [...topAbove, ...topBelow];
  const rivalSet = new Set(allRivals.map(r => r.accountId));

  songRivals.push(...allRivals);

  // Build song matches from samples
  for (const c of comboData) {
    for (const group of [c.above, c.below]) {
      for (const r of group) {
        if (!rivalSet.has(r.accountId)) continue;
        const info = seenAbove.get(r.accountId) ?? seenBelow.get(r.accountId)!;
        const matches: RivalSongMatch[] = [];
        for (const s of r.samples) {
          const songId = response.songs[s.s];
          if (!songId) continue;
          const coreInstrument = SERVER_TO_CORE_INSTRUMENT[s.i as ServerInstrumentKey];
          if (!coreInstrument) continue;

          const match: RivalSongMatch = {
            rival: info,
            songId,
            instrument: coreInstrument,
            userRank: s.ur,
            rivalRank: s.rr,
            rankDelta: s.ur - s.rr,
            userScore: s.us,
            rivalScore: s.rs,
          };
          matches.push(match);

          const key = `${songId}:${coreInstrument}`;
          const existing = closestRivalBySong.get(key);
          if (!existing || Math.abs(match.rankDelta) < Math.abs(existing.rankDelta)) {
            closestRivalBySong.set(key, match);
          }
        }
        // Merge matches (same rival may appear in multiple combos)
        const existing = byRival.get(r.accountId);
        if (existing) {
          existing.push(...matches);
        } else {
          byRival.set(r.accountId, matches);
        }
      }
    }
  }

  return {
    songRivals,
    leaderboardRivals: [],
    allRivals: [...songRivals],
    byRival,
    closestRivalBySong,
    leaderboardRivalIndex: new Map(),
  };
}
