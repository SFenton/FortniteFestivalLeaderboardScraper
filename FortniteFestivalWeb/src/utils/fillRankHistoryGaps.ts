import type { RankHistoryEntry, RankHistoryDeltaEntry } from '@festival/core/api/serverTypes';

/**
 * Fill gaps in sparse rank history using carry-forward semantics.
 * Each missing day inherits values from the most recent preceding entry.
 */
export function fillRankHistoryGaps(sparse: RankHistoryEntry[]): RankHistoryEntry[] {
  if (sparse.length <= 1) return sparse;

  const result: RankHistoryEntry[] = [];
  for (let i = 0; i < sparse.length; i++) {
    const current = sparse[i]!;
    result.push(current);

    if (i < sparse.length - 1) {
      const nextDate = new Date(sparse[i + 1]!.snapshotDate);
      const curDate = new Date(current.snapshotDate);
      // Fill gap days with carried-forward values
      curDate.setDate(curDate.getDate() + 1);
      while (curDate < nextDate) {
        result.push({ ...current, snapshotDate: formatDate(curDate) });
        curDate.setDate(curDate.getDate() + 1);
      }
    }
  }
  return result;
}

/**
 * Merge sparse base history + sparse delta history for leeway-adjusted chart.
 * Base entries are carry-forwarded; delta entries default to 0 on missing days.
 * Returns dense entries with effective (base + delta) ranks.
 */
export function mergeRankHistoryWithDeltas(
  baseHistory: RankHistoryEntry[],
  deltas: RankHistoryDeltaEntry[],
): RankHistoryEntry[] {
  if (baseHistory.length === 0) return [];

  // Index deltas by date for fast lookup
  const deltaMap = new Map<string, RankHistoryDeltaEntry>();
  for (const d of deltas) deltaMap.set(d.snapshotDate, d);

  // Gap-fill base, then apply deltas (default 0 for missing)
  const filled = fillRankHistoryGaps(baseHistory);
  return filled.map((entry) => {
    const d = deltaMap.get(entry.snapshotDate);
    if (!d) return entry;
    return {
      ...entry,
      adjustedSkillRank: entry.adjustedSkillRank + d.adjustedRankDelta,
      weightedRank: entry.weightedRank + d.weightedRankDelta,
      fcRateRank: entry.fcRateRank + d.fcRateRankDelta,
      totalScoreRank: entry.totalScoreRank + d.totalScoreRankDelta,
      maxScorePercentRank: entry.maxScorePercentRank + d.maxScoreRankDelta,
    };
  });
}

function formatDate(d: Date): string {
  const y = d.getFullYear();
  const m = String(d.getMonth() + 1).padStart(2, '0');
  const day = String(d.getDate()).padStart(2, '0');
  return `${y}-${m}-${day}`;
}
