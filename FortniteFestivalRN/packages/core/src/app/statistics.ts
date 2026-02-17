import type {InstrumentKey} from '../instruments';
import type {LeaderboardData, ScoreTracker} from '../models';
import type {SuggestionCategory, SuggestionSongItem} from '../suggestions/types';

export type InstrumentDetailedStats = {
  instrumentKey: InstrumentKey;
  instrumentLabel: string;
  totalSongsInLibrary: number;
  songsPlayed: number;
  songsUnplayed: number;
  completionPercent: number;
  fcCount: number;
  fcPercent: number;
  goldStarCount: number;
  fiveStarCount: number;
  fourStarCount: number;
  threeOrLessStarCount: number;
  averageStars: number;
  averageAccuracy: number;
  bestAccuracy: number;
  perfectScoreCount: number;
  totalScore: number;
  highestScore: number;
  averageScore: number;
  bestRank: number;
  averagePercentile: number;
  weightedPercentile: number;
  averagePercentileFormatted: string;
  weightedPercentileFormatted: string;
  bestRankFormatted: string;
  top1PercentCount: number;
  top5PercentCount: number;
  top10PercentCount: number;
  top25PercentCount: number;
  top50PercentCount: number;
  below50PercentCount: number;
};

export const instrumentKeysForStats: InstrumentKey[] = ['guitar', 'drums', 'vocals', 'bass', 'pro_guitar', 'pro_bass'];

const keyToLabel = (key: InstrumentKey): string => {
  switch (key) {
    case 'guitar':
      return 'Lead';
    case 'bass':
      return 'Bass';
    case 'drums':
      return 'Drums';
    case 'vocals':
      return 'Vocals';
    case 'pro_guitar':
      return 'Pro Lead';
    case 'pro_bass':
      return 'Pro Bass';
    default:
      return key;
  }
};

const selectTracker = (board: LeaderboardData, instrument: InstrumentKey): ScoreTracker | undefined => (board as any)[instrument] as
  | ScoreTracker
  | undefined;

const formatPercentile = (raw: number): string => {
  if (!Number.isFinite(raw) || raw <= 0) return 'N/A';
  const topPct = Math.max(1, Math.min(100, raw * 100));
  const bucket = Math.round(topPct);
  return `Top ${bucket}%`;
};

const weightScore = (tracker: ScoreTracker, baseline: number): number => {
  if (!tracker.rawPercentile || tracker.rawPercentile <= 0) return Number.POSITIVE_INFINITY;
  const entries = tracker.totalEntries > 0 ? tracker.totalEntries : tracker.calculatedNumEntries > 0 ? tracker.calculatedNumEntries : 1;
  return tracker.rawPercentile * (baseline / entries);
};

export const buildInstrumentStats = (params: {
  boards: ReadonlyArray<LeaderboardData>;
  totalSongsInLibrary: number;
}): InstrumentDetailedStats[] => {
  const out: InstrumentDetailedStats[] = [];

  for (const k of instrumentKeysForStats) {
    const trackers = params.boards.map(b => selectTracker(b, k)).filter(Boolean) as ScoreTracker[];
    const played = trackers.filter(t => (t.percentHit ?? 0) > 0);

    const songsPlayed = played.length;
    const totalSongsInLibrary = params.totalSongsInLibrary;

    const starsWithScore = played.filter(t => t.numStars > 0);

    const accuracies = played.map(t => t.percentHit / 10000);
    const scores = played.filter(t => t.maxScore > 0).map(t => t.maxScore);

    const ranked = played.filter(t => t.rank > 0);
    const percentiled = played.filter(t => t.rawPercentile > 0 && t.rawPercentile <= 1);

    const weightedComponents = percentiled.map(t => {
      const weight = t.totalEntries > 0 ? t.totalEntries : t.calculatedNumEntries > 0 ? t.calculatedNumEntries : 1;
      return {pct: t.rawPercentile, weight};
    });

    const weightedPercentile = (() => {
      if (weightedComponents.length === 0) return Number.NaN;
      const num = weightedComponents.reduce((acc, v) => acc + v.pct * v.weight, 0);
      const den = weightedComponents.reduce((acc, v) => acc + v.weight, 0);
      return den > 0 ? num / den : Number.NaN;
    })();

    const stats: InstrumentDetailedStats = {
      instrumentKey: k,
      instrumentLabel: keyToLabel(k),
      totalSongsInLibrary,
      songsPlayed,
      songsUnplayed: totalSongsInLibrary - songsPlayed,
      completionPercent: totalSongsInLibrary > 0 ? (songsPlayed * 100) / totalSongsInLibrary : 0,
      fcCount: played.filter(t => t.isFullCombo).length,
      fcPercent: songsPlayed > 0 ? (played.filter(t => t.isFullCombo).length * 100) / songsPlayed : 0,
      goldStarCount: played.filter(t => t.numStars === 6).length,
      fiveStarCount: played.filter(t => t.numStars === 5).length,
      fourStarCount: played.filter(t => t.numStars === 4).length,
      threeOrLessStarCount: played.filter(t => t.numStars > 0 && t.numStars <= 3).length,
      averageStars: starsWithScore.length > 0 ? starsWithScore.reduce((a, t) => a + t.numStars, 0) / starsWithScore.length : 0,
      averageAccuracy: accuracies.length > 0 ? accuracies.reduce((a, v) => a + v, 0) / accuracies.length : 0,
      bestAccuracy: accuracies.length > 0 ? Math.max(...accuracies) : 0,
      perfectScoreCount: played.filter(t => t.percentHit >= 1000000).length,
      totalScore: scores.reduce((a, v) => a + v, 0),
      highestScore: scores.length > 0 ? Math.max(...scores) : 0,
      averageScore: scores.length > 0 ? scores.reduce((a, v) => a + v, 0) / scores.length : 0,
      bestRank: ranked.length > 0 ? Math.min(...ranked.map(t => t.rank)) : 0,
      averagePercentile:
        percentiled.length > 0 ? percentiled.reduce((a, t) => a + t.rawPercentile, 0) / percentiled.length : Number.NaN,
      weightedPercentile,
      averagePercentileFormatted: formatPercentile(
        percentiled.length > 0 ? percentiled.reduce((a, t) => a + t.rawPercentile, 0) / percentiled.length : Number.NaN,
      ),
      weightedPercentileFormatted: formatPercentile(weightedPercentile),
      bestRankFormatted: ranked.length > 0 ? `#${Math.min(...ranked.map(t => t.rank)).toLocaleString()}` : 'N/A',
      top1PercentCount: 0,
      top5PercentCount: 0,
      top10PercentCount: 0,
      top25PercentCount: 0,
      top50PercentCount: 0,
      below50PercentCount: 0,
    };

    for (const t of percentiled) {
      const pct = t.rawPercentile * 100;
      if (pct <= 1) stats.top1PercentCount++;
      else if (pct <= 5) stats.top5PercentCount++;
      else if (pct <= 10) stats.top10PercentCount++;
      else if (pct <= 25) stats.top25PercentCount++;
      else if (pct <= 50) stats.top50PercentCount++;
      else stats.below50PercentCount++;
    }

    out.push(stats);
  }

  return out;
};

export const buildTopSongCategories = (params: {
  boards: ReadonlyArray<LeaderboardData>;
}): SuggestionCategory[] => {
  const out: SuggestionCategory[] = [];
  const boardList = [...params.boards];

  for (const k of instrumentKeysForStats) {
    const instrumentBoards = boardList.filter(ld => {
      const t = selectTracker(ld, k);
      return Boolean(t && t.rawPercentile > 0);
    });

    if (instrumentBoards.length === 0) continue;

    const weightsList = instrumentBoards
      .map(ld => {
        const t = selectTracker(ld, k)!;
        return t.totalEntries > 0 ? t.totalEntries : t.calculatedNumEntries > 0 ? t.calculatedNumEntries : 1;
      })
      .sort((a, b) => a - b);

    let baseline = weightsList[Math.floor(weightsList.length / 2)] ?? 1;
    if (baseline <= 0) baseline = 1;

    const weightedTopFive = [...instrumentBoards]
      .sort((a, b) => {
        const ta = selectTracker(a, k)!;
        const tb = selectTracker(b, k)!;
        const wa = weightScore(ta, baseline);
        const wb = weightScore(tb, baseline);
        if (wa !== wb) return wa - wb;
        return ta.rawPercentile - tb.rawPercentile;
      })
      .slice(0, 5);

    if (weightedTopFive.length > 0) {
      const songs: SuggestionSongItem[] = weightedTopFive.map(ld => {
        const t = selectTracker(ld, k)!;
        const weighted = weightScore(t, baseline);
        const pct = Number.isFinite(weighted) && weighted > 0 ? Math.max(0.01, Math.min(100, weighted * 100)) : undefined;
        return {
          songId: ld.songId,
          title: ld.title ?? '',
          artist: ld.artist ?? '',
          percent: pct,
        };
      });

      out.push({
        key: `stats_top_five_weighted_${k}`,
        title: `Top Five Songs (Weighted Percentile)`,
        description: `Your top five competitive songs (weighted by number of participants) for ${keyToLabel(k)}.`,
        songs,
      });
    }

    const topFive = [...instrumentBoards]
      .sort((a, b) => selectTracker(a, k)!.rawPercentile - selectTracker(b, k)!.rawPercentile)
      .slice(0, 5);

    if (topFive.length > 0) {
      const songs: SuggestionSongItem[] = topFive.map(ld => {
        const t = selectTracker(ld, k)!;
        const pct = Number.isFinite(t.rawPercentile) && t.rawPercentile > 0 ? Math.max(0.01, Math.min(100, t.rawPercentile * 100)) : undefined;
        return {
          songId: ld.songId,
          title: ld.title ?? '',
          artist: ld.artist ?? '',
          percent: pct,
        };
      });

      out.push({
        key: `stats_top_five_${k}`,
        title: `Top Five Songs (Raw Percentile)`,
        description: `Your top five competitive songs for ${keyToLabel(k)}.`,
        songs,
      });
    }
  }

  return out;
};
