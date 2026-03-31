import { useCallback, useMemo } from 'react';
import { useFestival } from '../../contexts/FestivalContext';
import { useSettings } from '../../contexts/SettingsContext';
import { type ServerInstrumentKey as InstrumentKey, type LeaderboardEntry, type PlayerScore, type ServerScoreHistoryEntry as ScoreHistoryEntry, type RankTier, type PopulationTierData } from '@festival/core/api/serverTypes';

/**
 * Binary search for the last entry where `entry.leeway <= target`.
 * Returns the entry, or undefined if none qualifies.
 */
function findTier<T extends { leeway: number }>(tiers: T[], targetLeeway: number): T | undefined {
  let lo = 0, hi = tiers.length - 1, result: T | undefined;
  while (lo <= hi) {
    const mid = (lo + hi) >>> 1;
    if (tiers[mid].leeway <= targetLeeway) {
      result = tiers[mid];
      lo = mid + 1;
    } else {
      hi = mid - 1;
    }
  }
  return result;
}

/**
 * Client-side invalid score filtering using precomputed minLeeway + validScores.
 *
 * When enabled, uses the `minLeeway` field on each score to determine validity
 * at the user's chosen leeway, and picks the best fallback from `validScores`.
 * Filtered rank and population are derived from precomputed changepoint tiers.
 *
 * Falls back to the legacy `isValid`/`validScore` fields when `minLeeway` is absent.
 */
export function useScoreFilter() {
  const { settings } = useSettings();
  const { state: { songs } } = useFestival();

  const enabled = settings.filterInvalidScores;
  const leeway = settings.filterInvalidScoresLeeway;

  // Build a lookup: songId → instrument → { maxThreshold, populationTiers }
  const thresholds = useMemo(() => {
    if (!enabled) return null;
    const map = new Map<string, Map<string, number>>();
    for (const song of songs) {
      if (!song.maxScores) continue;
      const instMap = new Map<string, number>();
      for (const [inst, maxScore] of Object.entries(song.maxScores)) {
        if (maxScore != null && maxScore > 0) {
          instMap.set(inst.toLowerCase(), maxScore * (1 + leeway / 100));
        }
      }
      if (instMap.size > 0) map.set(song.songId, instMap);
    }
    return map;
  }, [enabled, leeway, songs]);

  // Population tiers lookup: songId → instrument → PopulationTierData
  const popTiersLookup = useMemo(() => {
    const map = new Map<string, Map<string, PopulationTierData>>();
    for (const song of songs) {
      if (!song.populationTiers) continue;
      const instMap = new Map<string, PopulationTierData>();
      for (const [inst, tierData] of Object.entries(song.populationTiers)) {
        if (tierData) instMap.set(inst.toLowerCase(), tierData);
      }
      if (instMap.size > 0) map.set(song.songId, instMap);
    }
    return map;
  }, [songs]);

  /** Check if a single score is valid at current leeway. */
  const isScoreValid = useCallback(
    (songId: string, instrument: InstrumentKey | string, score: number): boolean => {
      if (!thresholds) return true;
      const instMap = thresholds.get(songId);
      if (!instMap) return true;
      const threshold = instMap.get(instrument.toLowerCase());
      if (threshold == null) return true;
      return score <= threshold;
    },
    [thresholds],
  );

  /** Get the filtered total entries for a (songId, instrument) at current leeway. */
  const getFilteredTotal = useCallback(
    (songId: string, instrument: InstrumentKey | string, unfilteredTotal?: number): number | undefined => {
      const instMap = popTiersLookup.get(songId);
      if (!instMap) return unfilteredTotal;
      const tierData = instMap.get(instrument.toLowerCase());
      if (!tierData) return unfilteredTotal;
      const tier = findTier(tierData.tiers, leeway);
      return tier ? tier.total : tierData.baseCount;
    },
    [popTiersLookup, leeway],
  );

  /** Get the filtered rank for a fallback score at current leeway. */
  const getFilteredRank = useCallback(
    (rankTiers: RankTier[] | null | undefined): number | undefined => {
      if (!rankTiers || rankTiers.length === 0) return undefined;
      const tier = findTier(rankTiers, leeway);
      return tier?.rank;
    },
    [leeway],
  );

  /** Filter leaderboard entries and re-rank. */
  const filterLeaderboard = useCallback(
    (songId: string, instrument: InstrumentKey | string, entries: LeaderboardEntry[], startRank = 1): LeaderboardEntry[] => {
      if (!thresholds) return entries;
      const filtered = entries.filter(e => isScoreValid(songId, instrument, e.score));
      return filtered.map((e, i) => ({ ...e, rank: startRank + i }));
    },
    [thresholds, isScoreValid],
  );

  /**
   * Filter player scores using precomputed minLeeway + validScores.
   * For each score:
   * - If minLeeway <= userLeeway → score is valid, use as-is
   * - Else → find best validScores entry where minLeeway <= userLeeway
   * - If found → substitute score/accuracy/fc/stars + use rankTiers for filtered rank
   * - If not found → omit (invalid with no valid fallback at this leeway)
   *
   * Falls back to legacy isValid/validScore fields when minLeeway is absent.
   */
  const filterPlayerScores = useCallback(
    (scores: PlayerScore[]): PlayerScore[] => {
      if (!thresholds) return scores;
      const out: PlayerScore[] = [];
      for (const s of scores) {
        // New path: use minLeeway if present
        if (s.minLeeway != null) {
          if (s.minLeeway <= leeway) {
            // Score is valid at current leeway
            out.push(s);
          } else if (s.validScores && s.validScores.length > 0) {
            // Find best fallback where minLeeway <= userLeeway
            const fallback = s.validScores.find(v => v.minLeeway <= leeway);
            if (fallback) {
              const filteredRank = getFilteredRank(fallback.rankTiers);
              const filteredTotal = getFilteredTotal(s.songId, s.instrument, s.totalEntries);
              out.push({
                ...s,
                score: fallback.score,
                accuracy: fallback.accuracy ?? s.accuracy,
                isFullCombo: fallback.fc ?? s.isFullCombo,
                stars: fallback.stars ?? s.stars,
                rank: filteredRank ?? s.rank,
                totalEntries: filteredTotal ?? s.totalEntries,
              });
            }
            // else: no valid fallback at this leeway — omit
          }
          // else: invalid, no validScores array — omit
          continue;
        }

        // Legacy path: use isScoreValid + validScore fields
        if (isScoreValid(s.songId, s.instrument, s.score)) {
          out.push(s);
        } else if (s.validScore != null) {
          out.push({
            ...s,
            score: s.validScore,
            rank: s.validRank ?? 0,
            accuracy: s.validAccuracy ?? s.accuracy,
            isFullCombo: s.validIsFullCombo ?? s.isFullCombo,
            stars: s.validStars ?? s.stars,
            totalEntries: s.validTotalEntries ?? s.totalEntries,
          });
        }
        // else: invalid with no fallback — omit
      }
      return out;
    },
    [thresholds, leeway, isScoreValid, getFilteredRank, getFilteredTotal],
  );

  /** Filter score history entries. */
  const filterHistory = useCallback(
    (songId: string, instrument: InstrumentKey | string, history: ScoreHistoryEntry[]): ScoreHistoryEntry[] => {
      if (!thresholds) return history;
      return history.filter(h => isScoreValid(songId, instrument, h.newScore));
    },
    [thresholds, isScoreValid],
  );

  /**
   * Leeway value to pass to server API endpoints.
   * Returns undefined when filtering is disabled (omit from API call).
   */
  const leewayParam: number | undefined = enabled ? leeway : undefined;

  return useMemo(
    () => ({ enabled, leeway, leewayParam, isScoreValid, getFilteredTotal, getFilteredRank, filterLeaderboard, filterPlayerScores, filterHistory }),
    [enabled, leeway, leewayParam, isScoreValid, getFilteredTotal, getFilteredRank, filterLeaderboard, filterPlayerScores, filterHistory],
  );
}
