import { useEffect, useState, useMemo } from 'react';
import { api } from '../../api/client';
import type { InstrumentKey, ScoreHistoryEntry } from '../../models';

export type ChartPoint = {
  date: string;
  dateLabel: string;
  timestamp: number;
  score: number;
  accuracy: number;
  isFullCombo: boolean;
  colorAccuracy?: number;
  stars?: number;
  season?: number;
};

/** Simple in-memory cache so navigating between songs doesn't re-fetch. */
const historyCache = new Map<string, ScoreHistoryEntry[]>();

export function clearHistoryCache() {
  historyCache.clear();
}

/**
 * Fetches (or caches) score history for a song/account pair, filters by
 * instrument, and transforms into chart-ready data points.
 */
export function useChartData(
  accountId: string,
  songId: string,
  instrument: InstrumentKey,
  historyProp?: ScoreHistoryEntry[],
) {
  const cacheKey = `${accountId}:${songId}`;
  const [songHistory, setSongHistory] = useState<ScoreHistoryEntry[]>(
    () => historyProp ?? historyCache.get(cacheKey) ?? [],
  );
  const [loading, setLoading] = useState(!historyProp && !historyCache.has(cacheKey));

  useEffect(() => {
    if (historyProp) {
      setSongHistory(prev => prev === historyProp ? prev : historyProp);
      historyCache.set(cacheKey, historyProp);
      setLoading(false);
      return;
    }
    if (historyCache.has(cacheKey)) {
      setSongHistory(historyCache.get(cacheKey)!);
      setLoading(false);
      return;
    }
    let cancelled = false;
    setLoading(true);
    api.getPlayerHistory(accountId, songId)
      .then((res) => { if (!cancelled) { historyCache.set(cacheKey, res.history); setSongHistory(res.history); } })
      .catch(() => { if (!cancelled) setSongHistory([]); })
      .finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [accountId, songId, cacheKey, historyProp]);

  const filtered = useMemo(
    () => songHistory.filter(h => h.instrument === instrument),
    [songHistory, instrument],
  );

  const chartData: ChartPoint[] = useMemo(() => {
    const sorted = [...filtered].sort(
      (a, b) => new Date(a.scoreAchievedAt ?? a.changedAt).getTime() - new Date(b.scoreAchievedAt ?? b.changedAt).getTime(),
    );
    const daySeen = new Map<string, number>();
    const dayTotal = new Map<string, number>();
    const formatDay = (d: Date) => `${d.getMonth() + 1}/${d.getDate()}/${String(d.getFullYear()).slice(-2)}`;
    for (const h of sorted) {
      const dayKey = formatDay(new Date(h.scoreAchievedAt ?? h.changedAt));
      dayTotal.set(dayKey, (dayTotal.get(dayKey) ?? 0) + 1);
    }
    return sorted.map((h) => {
      const d = new Date(h.scoreAchievedAt ?? h.changedAt);
      const dayKey = formatDay(d);
      const idx = (daySeen.get(dayKey) ?? 0) + 1;
      daySeen.set(dayKey, idx);
      return {
        date: d.toISOString(),
        dateLabel: dayKey,
        timestamp: d.getTime(),
        score: h.newScore,
        accuracy: h.accuracy != null ? h.accuracy / 10000 : 0,
        isFullCombo: h.isFullCombo ?? false,
        stars: h.stars ?? undefined,
        season: h.season ?? undefined,
      };
    });
  }, [filtered]);

  /** Count per-instrument entries for the instrument picker. */
  const instrumentCounts = useMemo(() => {
    const counts: Partial<Record<InstrumentKey, number>> = {};
    for (const h of songHistory) {
      const key = h.instrument as InstrumentKey;
      counts[key] = (counts[key] ?? 0) + 1;
    }
    return counts;
  }, [songHistory]);

  return { songHistory, chartData, loading, instrumentCounts };
}
