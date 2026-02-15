import type {V1LeaderboardEntry, V1LeaderboardPage} from '../models';
import {ScoreTracker} from '../models';

export const buildV1LeaderboardUrl = (params: {
  songId: string;
  api: string;
  accountId: string;
  page?: number;
}): string => {
  const page = params.page ?? 0;
  // Mirrors FestivalService.FetchInstrumentAsync
  return (
    `/api/v1/leaderboards/FNFestival/alltime_${params.songId}_${params.api}` +
    `/alltime/${params.accountId}` +
    `?page=${page}&rank=0&teamAccountIds=${params.accountId}` +
    `&appId=Fortnite&showLiveSessions=false`
  );
};

const asNumber = (v: unknown): number | undefined => (typeof v === 'number' && Number.isFinite(v) ? v : undefined);
const asString = (v: unknown): string | undefined => (typeof v === 'string' ? v : undefined);

export const parseV1LeaderboardPage = (body: string | null | undefined): V1LeaderboardPage | null => {
  if (!body) return null;
  const trimmed = body.trim();
  if (!trimmed.startsWith('{')) return null;

  try {
    const root = JSON.parse(trimmed) as Record<string, unknown>;
    const page = asNumber(root.page) ?? 0;
    const totalPages = asNumber(root.totalPages) ?? 0;
    const entriesRaw = Array.isArray(root.entries) ? root.entries : [];
    const entries: V1LeaderboardEntry[] = [];

    for (const e of entriesRaw) {
      if (!e || typeof e !== 'object') continue;
      const obj = e as Record<string, unknown>;
      const entry: V1LeaderboardEntry = {};
      entry.team_id = asString(obj.team_id) ?? asString(obj.teamId);
      entry.rank = asNumber(obj.rank);
      entry.pointsEarned = asNumber(obj.pointsEarned);
      entry.percentile = asNumber(obj.percentile);

      // sessionHistory parsing (only trackedStats fields we care about)
      const sh = Array.isArray(obj.sessionHistory) ? obj.sessionHistory : [];
      if (sh.length > 0) {
        entry.sessionHistory = [];
        let bestScore = 0;
        for (const s of sh) {
          const h: any = {};
          if (s && typeof s === 'object') {
            const tracked = (s as any).trackedStats;
            if (tracked && typeof tracked === 'object') {
              const ts: any = {};
              if (asNumber(tracked.SCORE) != null) ts.SCORE = tracked.SCORE;
              if (asNumber(tracked.ACCURACY) != null) ts.ACCURACY = tracked.ACCURACY;
              if (asNumber(tracked.FULL_COMBO) != null) ts.FULL_COMBO = tracked.FULL_COMBO;
              if (asNumber(tracked.STARS_EARNED) != null) ts.STARS_EARNED = tracked.STARS_EARNED;
              if (asNumber(tracked.SEASON) != null) ts.SEASON = tracked.SEASON;
              if (asNumber(tracked.DIFFICULTY) != null) ts.DIFFICULTY = tracked.DIFFICULTY;
              h.trackedStats = ts;
              if (typeof ts.SCORE === 'number' && ts.SCORE > bestScore) bestScore = ts.SCORE;
            }
          }
          entry.sessionHistory.push(h);
        }
        entry.score = bestScore;
      }

      entries.push(entry);
    }

    return {page, totalPages, entries};
  } catch {
    return null;
  }
};

export const updateTrackerFromV1 = (params: {
  page: V1LeaderboardPage;
  accountId: string;
  difficulty: number;
  existing?: ScoreTracker;
}): ScoreTracker => {
  const tracker = params.existing ?? new ScoreTracker();
  const prevScore = tracker.maxScore;
  const prevRank = tracker.rank;
  const prevRawPct = tracker.rawPercentile;

  tracker.difficulty = params.difficulty;

  const entry = params.page.entries.find(
    e => (e.team_id ?? '').toLowerCase() === params.accountId.toLowerCase(),
  );
  if (!entry) {
    tracker.refreshDerived();
    return tracker;
  }

  const bestScore = typeof entry.score === 'number' ? entry.score : 0;
  const rank = typeof entry.rank === 'number' ? entry.rank : 0;

  tracker.rank = rank;
  if (bestScore > tracker.maxScore) tracker.maxScore = bestScore;
  tracker.initialized = tracker.maxScore > 0 || tracker.rank > 0;

  // pick stats for the bestScore
  const history = entry.sessionHistory ?? [];
  const bestStats = history
    .map(h => (h as any)?.trackedStats as any)
    .find(ts => ts && typeof ts.SCORE === 'number' && ts.SCORE === bestScore);
  if (bestStats) {
    if (typeof bestStats.ACCURACY === 'number') tracker.percentHit = bestStats.ACCURACY;
    if (typeof bestStats.FULL_COMBO === 'number') tracker.isFullCombo = bestStats.FULL_COMBO === 1;
    if (typeof bestStats.STARS_EARNED === 'number') tracker.numStars = bestStats.STARS_EARNED;
    if (typeof bestStats.SEASON === 'number' && bestStats.SEASON > 0) tracker.seasonAchieved = bestStats.SEASON;
    if (typeof bestStats.DIFFICULTY === 'number' && bestStats.DIFFICULTY >= 0 && bestStats.DIFFICULTY <= 3) {
      tracker.gameDifficulty = bestStats.DIFFICULTY as 0 | 1 | 2 | 3;
    }
  }

  if (typeof entry.percentile === 'number' && entry.percentile > 0) tracker.rawPercentile = entry.percentile;
  if (tracker.rank > 0 && tracker.rawPercentile > 1e-9) {
    let estimate = Math.round(tracker.rank / tracker.rawPercentile);
    if (estimate < tracker.rank) estimate = tracker.rank;
    if (estimate > 10_000_000) estimate = 10_000_000;
    tracker.calculatedNumEntries = estimate;
  }

  // Keep side-effectful formatting consistent
  tracker.refreshDerived();

  // Return same instance, but ensure we touched all important fields
  // (prev variables used for parity/diagnostics in service; not needed here)
  void prevScore;
  void prevRank;
  void prevRawPct;

  return tracker;
};
