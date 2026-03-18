/**
 * Hook that sorts score history entries by a given mode and direction.
 * Extracted from PlayerHistoryPage for testability.
 */
import { useMemo } from 'react';
import { PlayerScoreSortMode } from '@festival/core';
import { type ServerScoreHistoryEntry as ScoreHistoryEntry } from '@festival/core/api/serverTypes';

export function useSortedScoreHistory(
  filteredHistory: ScoreHistoryEntry[],
  sortMode: PlayerScoreSortMode,
  sortAscending: boolean,
): ScoreHistoryEntry[] {
  return useMemo(() => {
    const arr = [...filteredHistory];
    const dir = sortAscending ? 1 : -1;
    arr.sort((a, b) => {
      switch (sortMode) {
        case PlayerScoreSortMode.Date: {
          const da = a.scoreAchievedAt ?? a.changedAt ?? '';
          const db = b.scoreAchievedAt ?? b.changedAt ?? '';
          return dir * da.localeCompare(db);
        }
        case PlayerScoreSortMode.Score:
          return dir * (a.newScore - b.newScore);
        case PlayerScoreSortMode.Accuracy: {
          const cmp = dir * ((a.accuracy ?? 0) - (b.accuracy ?? 0));
          if (cmp !== 0) return cmp;
          const fcA = a.isFullCombo ? 1 : 0;
          const fcB = b.isFullCombo ? 1 : 0;
          if (fcA !== fcB) return dir * (fcA - fcB);
          if (a.newScore !== b.newScore) return dir * (a.newScore - b.newScore);
          const da = a.scoreAchievedAt ?? a.changedAt ?? '';
          const db = b.scoreAchievedAt ?? b.changedAt ?? '';
          return dir * da.localeCompare(db);
        }
        case PlayerScoreSortMode.Season:
          return dir * ((a.season ?? 0) - (b.season ?? 0));
        default:
          return 0;
      }
    });
    return arr;
  }, [filteredHistory, sortMode, sortAscending]);
}
