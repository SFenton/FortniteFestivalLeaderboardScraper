/**
 * Pure computation functions for PlayerPage statistics.
 * Extracted for readability and potential reuse.
 */
import { formatPercentileBucket, accuracyColor } from '@festival/core';
import type { PlayerScore, InstrumentKey } from '../../models';

export { accuracyColor };

export type InstrumentStats = {
  songsPlayed: number;
  completionPercent: string;
  fcCount: number;
  fcPercent: string;
  goldStarCount: number;
  fiveStarCount: number;
  fourStarCount: number;
  threeStarCount: number;
  twoStarCount: number;
  oneStarCount: number;
  averageStars: number;
  avgAccuracy: number;
  bestAccuracy: number;
  avgScore: number;
  bestRank: number;
  bestRankSongId: string | null;
  overallPercentile: string;
  avgPercentile: string;
  percentileBuckets: { pct: number; count: number }[];
};

export function computeInstrumentStats(scores: PlayerScore[], totalSongs: number): InstrumentStats {
  const n = scores.length;
  const fcCount = scores.filter(s => s.isFullCombo).length;
  const goldStars = scores.filter(s => (s.stars ?? 0) >= 6).length;
  const fiveStars = scores.filter(s => (s.stars ?? 0) === 5).length;
  const fourStars = scores.filter(s => (s.stars ?? 0) === 4).length;
  const threeStars = scores.filter(s => (s.stars ?? 0) === 3).length;
  const twoStars = scores.filter(s => (s.stars ?? 0) === 2).length;
  const oneStars = scores.filter(s => (s.stars ?? 0) === 1).length;

  const starsWithScore = scores.filter(s => (s.stars ?? 0) > 0);
  const averageStars = starsWithScore.length > 0
    ? starsWithScore.reduce((a, s) => a + (s.stars ?? 0), 0) / starsWithScore.length : 0;

  const accuracies = scores.map(s => s.accuracy ?? 0).filter(a => a > 0);
  const avgAcc = accuracies.length > 0 ? accuracies.reduce((a, b) => a + b, 0) / accuracies.length : 0;
  const bestAcc = accuracies.length > 0 ? Math.max(...accuracies) : 0;
  const avgScore = n > 0 ? scores.reduce((a, b) => a + b.score, 0) / n : 0;
  const rankedScores = scores.filter(s => s.rank > 0);
  const bestRank = rankedScores.length > 0 ? Math.min(...rankedScores.map(s => s.rank)) : 0;
  const bestRankSongId = bestRank > 0 ? (rankedScores.find(s => s.rank === bestRank)?.songId ?? null) : null;

  const percentiled = scores
    .filter(s => s.rank > 0 && (s.totalEntries ?? 0) > 0)
    .map(s => ({ pct: s.rank / s.totalEntries!, weight: s.totalEntries! }));

  let overallPercentile = '—';
  let avgPercentile = '—';
  if (percentiled.length > 0) {
    const avgPlayed = (percentiled.reduce((a, v) => a + v.pct, 0) / percentiled.length) * 100;
    avgPercentile = formatPercentileBucket(avgPlayed);
    const unplayedCount = totalSongs - n;
    const totalPct = percentiled.reduce((a, v) => a + v.pct, 0) + unplayedCount;
    const overall = (totalPct / totalSongs) * 100;
    overallPercentile = formatPercentileBucket(overall);
  }

  const pctThresholds = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100];
  const percentileBuckets: { pct: number; count: number }[] = [];
  for (const t of pctThresholds) {
    const prev = pctThresholds[pctThresholds.indexOf(t) - 1] ?? 0;
    let count = 0;
    for (const { pct } of percentiled) {
      const pctVal = pct * 100;
      if (pctVal > prev && pctVal <= t) count++;
    }
    if (count > 0) percentileBuckets.push({ pct: t, count });
  }

  return {
    songsPlayed: n, completionPercent: totalSongs > 0 ? ((n / totalSongs) * 100).toFixed(1) : '0',
    fcCount, fcPercent: n > 0 ? (Math.floor((fcCount / n) * 1000) / 10).toFixed(1) : '0',
    goldStarCount: goldStars, fiveStarCount: fiveStars, fourStarCount: fourStars,
    threeStarCount: threeStars, twoStarCount: twoStars, oneStarCount: oneStars,
    averageStars, avgAccuracy: avgAcc, bestAccuracy: bestAcc, avgScore,
    bestRank, bestRankSongId, overallPercentile, avgPercentile, percentileBuckets,
  };
}

export function computeOverallStats(scores: PlayerScore[]) {
  const uniqueSongs = new Set(scores.map(s => s.songId));
  const fcCount = scores.filter(s => s.isFullCombo).length;
  const goldStars = scores.filter(s => (s.stars ?? 0) >= 6).length;
  const totalScore = scores.reduce((a, b) => a + b.score, 0);
  const accuracies = scores.map(s => s.accuracy ?? 0).filter(a => a > 0);
  const avgAcc = accuracies.length > 0 ? accuracies.reduce((a, b) => a + b, 0) / accuracies.length : 0;
  const rankedScores = scores.filter(s => s.rank > 0);
  const bestRank = rankedScores.length > 0 ? Math.min(...rankedScores.map(s => s.rank)) : 0;
  const bestRankScore = bestRank > 0 ? rankedScores.find(s => s.rank === bestRank) : undefined;
  return {
    totalScore, songsPlayed: uniqueSongs.size, fcCount,
    fcPercent: scores.length > 0 ? (Math.floor((fcCount / scores.length) * 1000) / 10).toFixed(1) : '0',
    goldStarCount: goldStars, avgAccuracy: avgAcc, bestRank,
    bestRankSongId: bestRankScore?.songId ?? null,
    bestRankInstrument: bestRankScore?.instrument ?? null,
  };
}

export function groupByInstrument(scores: PlayerScore[]) {
  const map = new Map<InstrumentKey, PlayerScore[]>();
  for (const s of scores) {
    const key = s.instrument as InstrumentKey;
    if (!map.has(key)) map.set(key, []);
    map.get(key)!.push(s);
  }
  return map;
}

export function formatClamped(val: number): string {
  const floored = Math.floor(val * 10) / 10;
  const fixed = floored.toFixed(1);
  return fixed.endsWith('.0') ? Math.floor(val).toString() : fixed;
}

export function formatClamped2(val: number): string {
  const fixed = val.toFixed(2);
  if (fixed.endsWith('00')) return fixed.slice(0, -3);
  if (fixed.endsWith('0')) return fixed.slice(0, -1);
  return fixed;
}
