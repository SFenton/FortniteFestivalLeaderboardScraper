import type { RankHistoryEntry, RankHistoryDeltaEntry } from '@festival/core/api/serverTypes';

const DATE_ONLY_RE = /^(\d{4})-(\d{2})-(\d{2})$/;

export function parseSnapshotDate(snapshotDate: string): Date {
  const match = DATE_ONLY_RE.exec(snapshotDate);
  if (!match) return new Date(snapshotDate);

  const year = Number(match[1]);
  const month = Number(match[2]) - 1;
  const day = Number(match[3]);

  // Use local noon so date-only snapshots stay on the intended calendar day.
  return new Date(year, month, day, 12, 0, 0, 0);
}

function localCalendarDate(value: Date): Date {
  return new Date(value.getFullYear(), value.getMonth(), value.getDate(), 12, 0, 0, 0);
}

/**
 * Fill gaps in sparse rank history using carry-forward semantics.
 * Each missing day inherits values from the most recent preceding entry,
 * including trailing days through the local current calendar day.
 */
export function fillRankHistoryGaps(sparse: RankHistoryEntry[], now = new Date()): RankHistoryEntry[] {
  if (sparse.length === 0) return sparse;

  const result: RankHistoryEntry[] = [];
  for (let i = 0; i < sparse.length; i++) {
    const current = sparse[i]!;
    result.push({ ...current, isSynthetic: current.isSynthetic ?? false });

    if (i < sparse.length - 1) {
      const nextDate = parseSnapshotDate(sparse[i + 1]!.snapshotDate);
      const curDate = parseSnapshotDate(current.snapshotDate);
      // Fill gap days with carried-forward values
      curDate.setDate(curDate.getDate() + 1);
      while (curDate < nextDate) {
        result.push({ ...current, snapshotDate: formatDate(curDate), snapshotTakenAt: null, isSynthetic: true });
        curDate.setDate(curDate.getDate() + 1);
      }
    }
  }

  const lastEntry = result[result.length - 1]!;
  const trailingDate = parseSnapshotDate(lastEntry.snapshotDate);
  const today = localCalendarDate(now);

  trailingDate.setDate(trailingDate.getDate() + 1);
  while (trailingDate <= today) {
    result.push({ ...lastEntry, snapshotDate: formatDate(trailingDate), snapshotTakenAt: null, isSynthetic: true });
    trailingDate.setDate(trailingDate.getDate() + 1);
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
  now = new Date(),
): RankHistoryEntry[] {
  if (baseHistory.length === 0) return [];

  // Index deltas by date for fast lookup
  const deltaMap = new Map<string, RankHistoryDeltaEntry>();
  for (const d of deltas) deltaMap.set(d.snapshotDate, d);

  // Gap-fill base, then apply deltas (default 0 for missing)
  const filled = fillRankHistoryGaps(baseHistory, now);
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
