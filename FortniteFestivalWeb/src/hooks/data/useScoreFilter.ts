import { useCallback, useMemo } from 'react';
import { useFestival } from '../../contexts/FestivalContext';
import { useSettings } from '../../contexts/SettingsContext';
import { type ServerInstrumentKey as InstrumentKey, type LeaderboardEntry, type PlayerScore, type ServerScoreHistoryEntry as ScoreHistoryEntry } from '@festival/core/api/serverTypes';

/**
 * Client-side invalid score filtering based on CHOpt max scores + user leeway %.
 *
 * When enabled, scores exceeding `maxScore * (1 + leeway/100)` are treated as
 * invalid and removed. Ranks are recomputed sequentially on the filtered list.
 */
export function useScoreFilter() {
  const { settings } = useSettings();
  const { state: { songs } } = useFestival();

  const enabled = settings.filterInvalidScores;
  const leeway = settings.filterInvalidScoresLeeway;

  // Build a lookup: songId → instrument → max score threshold
  // API returns keys like "solo_Guitar" but InstrumentKey uses "Solo_Guitar",
  // so we normalize with a case-insensitive lookup.
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

  /** Check if a single score is valid (or if there's no max score data to judge). */
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

  /** Filter leaderboard entries and re-rank starting from `startRank` (1-based, default 1). */
  const filterLeaderboard = useCallback(
    (songId: string, instrument: InstrumentKey | string, entries: LeaderboardEntry[], startRank = 1): LeaderboardEntry[] => {
      if (!thresholds) return entries;
      const filtered = entries.filter(e => isScoreValid(songId, instrument, e.score));
      return filtered.map((e, i) => ({ ...e, rank: startRank + i }));
    },
    [thresholds, isScoreValid],
  );

  /** Filter player scores: substitute server-provided fallback values for invalid
   *  scores, only removing entries that are invalid AND have no fallback. */
  const filterPlayerScores = useCallback(
    (scores: PlayerScore[]): PlayerScore[] => {
      if (!thresholds) return scores;
      const out: PlayerScore[] = [];
      for (const s of scores) {
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
    [thresholds, isScoreValid],
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
    () => ({ enabled, leewayParam, isScoreValid, filterLeaderboard, filterPlayerScores, filterHistory }),
    [enabled, leewayParam, isScoreValid, filterLeaderboard, filterPlayerScores, filterHistory],
  );
}
