/**
 * Hook that encapsulates the song list filtering and sorting logic.
 * Extracted from SongsPage for readability and potential reuse.
 */
import { useMemo } from 'react';
import { type ServerSong as Song, type PlayerScore, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import type { SongFilters, SongSortMode } from '../../utils/songSettings';
import { compareByMode } from '../../utils/songSort';
import { getSongInstrumentDifficulty, songSupportsInstrument } from '../../utils/songInstrumentDifficulty';

interface FilterSortOptions {
  songs: Song[];
  search: string;
  sortMode: SongSortMode;
  sortAscending: boolean;
  filters: SongFilters;
  instrument: InstrumentKey | null;
  /** Map: songId → PlayerScore for the currently selected instrument */
  scoreMap: Map<string, PlayerScore>;
  /** Map: songId → Map<InstrumentKey, PlayerScore> for all instruments */
  allScoreMap: Map<string, Map<InstrumentKey, PlayerScore>>;
  /** Set of songIds currently in the item shop (for 'shop' sort mode). */
  shopSongIds?: ReadonlySet<string> | null;
  /** Set of in-shop songIds whose offer expires tomorrow. */
  leavingTomorrowIds?: ReadonlySet<string> | null;
  /** Callback to check whether a score is within the CHOpt max threshold. */
  isScoreValid?: (songId: string, instrument: InstrumentKey | string, score: number) => boolean;
  /** Whether the "Filter Invalid Scores" app setting is enabled (gates overThreshold filter). */
  filterInvalidScoresEnabled?: boolean;
  /** Whether the Item Shop feature is visible (gates shopInShop/shopLeavingTomorrow filters). */
  shopVisible?: boolean;
}

const PCT_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100];

function getSongIntensity(song: Song, instrument: InstrumentKey | null): number | undefined {
  return getSongInstrumentDifficulty(song, instrument);
}

export function useFilteredSongs({
  songs,
  search,
  sortMode,
  sortAscending,
  filters: f,
  instrument: inst,
  scoreMap,
  allScoreMap,
  shopSongIds,
  leavingTomorrowIds,
  isScoreValid,
  filterInvalidScoresEnabled,
  shopVisible,
}: FilterSortOptions): Song[] {
  return useMemo(() => {
    const q = search.toLowerCase();
    const hasPlayerData = allScoreMap.size > 0;

    // Pre-compute which instruments have any missing/has filter active
    const filterInstruments = new Set<InstrumentKey>();
    for (const [k, v] of Object.entries(f.missingScores)) { if (v) filterInstruments.add(k as InstrumentKey); }
    for (const [k, v] of Object.entries(f.missingFCs)) { if (v) filterInstruments.add(k as InstrumentKey); }
    for (const [k, v] of Object.entries(f.hasScores)) { if (v) filterInstruments.add(k as InstrumentKey); }
    for (const [k, v] of Object.entries(f.hasFCs)) { if (v) filterInstruments.add(k as InstrumentKey); }
    if (filterInvalidScoresEnabled) {
      for (const [k, v] of Object.entries(f.overThreshold ?? {})) { if (v) filterInstruments.add(k as InstrumentKey); }
    }
    const activeFilterInstruments = inst
      ? (filterInstruments.has(inst) ? [inst] : [])
      : [...filterInstruments];

    // Pre-compute which filter checks are active
    // Season/percentile/stars/difficulty filters are instrument-specific — skip them when no instrument is selected
    // so stale filter values from a previously selected instrument don't hide songs.
    const seasonKeys = Object.keys(f.seasonFilter);
    const checkSeason = inst != null && hasPlayerData && seasonKeys.length > 0 && seasonKeys.some(k => f.seasonFilter[Number(k)] === false);
    const pctKeys = Object.keys(f.percentileFilter);
    const checkPct = inst != null && hasPlayerData && pctKeys.length > 0 && pctKeys.some(k => f.percentileFilter[Number(k)] === false);
    const starKeys = Object.keys(f.starsFilter);
    const checkStars = inst != null && hasPlayerData && starKeys.length > 0 && starKeys.some(k => f.starsFilter[Number(k)] === false);
    const diffKeys = Object.keys(f.difficultyFilter);
    const checkDiff = inst != null && diffKeys.length > 0 && diffKeys.some(k => f.difficultyFilter[Number(k)] === false);

    const list = songs.filter(s => {
      if (q && !s.title.toLowerCase().includes(q) && !s.artist.toLowerCase().includes(q)) return false;

      // Item Shop filters (independent of player data)
      // AND logic: both must pass when both enabled; leaving tomorrow ⊂ in shop
      if (shopVisible && (f.shopInShop || f.shopLeavingTomorrow)) {
        if (f.shopInShop && !shopSongIds?.has(s.songId)) return false;
        if (f.shopLeavingTomorrow && !leavingTomorrowIds?.has(s.songId)) return false;
      }

      if (inst && !songSupportsInstrument(s, inst)) return false;

      if (!hasPlayerData) {
        if (checkDiff) {
          const diff = getSongIntensity(s, inst);
          const difficultyBucket = diff == null ? 0 : Math.max(1, Math.min(7, Math.trunc(diff) + 1));
          if (f.difficultyFilter[difficultyBucket] === false) return false;
        }

        return true;
      }

      const byInst = allScoreMap.get(s.songId);

      // Per-instrument filters
      if (activeFilterInstruments.length > 0) {
        let anyInstrumentPassed = false;
        for (const key of activeFilterInstruments) {
          if (!songSupportsInstrument(s, key)) continue;

          const ps = byInst?.get(key);
          const hasScore = !!ps?.score;
          const hasFC = !!ps?.isFullCombo;
          const ms = f.missingScores[key];
          const hs = f.hasScores[key];
          const mf = f.missingFCs[key];
          const hf = f.hasFCs[key];
          let passed = true;
          if (ms || hs) {
            if (!(ms && !hasScore) && !(hs && hasScore)) passed = false;
          }
          if (passed && (mf || hf)) {
            if (!(mf && !hasFC) && !(hf && hasFC)) passed = false;
          }
          if (passed && filterInvalidScoresEnabled && f.overThreshold?.[key] && isScoreValid) {
            const over = ps?.score != null && ps.score > 0 && !isScoreValid(s.songId, key, ps.score);
            if (!over) passed = false;
          }
          if (passed) { anyInstrumentPassed = true; break; }
        }
        if (!anyInstrumentPassed) return false;
      }

      const score = scoreMap.get(s.songId);

      if (checkSeason) {
        const season = score?.season ?? 0;
        if (f.seasonFilter[season] === false) return false;
      }
      if (checkPct) {
        if (!score) {
          if (f.percentileFilter[0] === false) return false;
        } else {
          const pct = score.rank > 0 && (score.totalEntries ?? 0) > 0
            ? Math.min((score.rank / score.totalEntries!) * 100, 100) : undefined;
          if (pct == null) {
            if (f.percentileFilter[0] === false) return false;
          } else {
            /* v8 ignore start -- pct capped at 100 by Math.min */
            const bracket = PCT_THRESHOLDS.find(t => pct <= t) ?? 100;
            /* v8 ignore stop */
            if (f.percentileFilter[bracket] === false) return false;
          }
        }
      }
      if (checkStars) {
        const stars = score?.stars ?? 0;
        if (f.starsFilter[stars] === false) return false;
      }
      if (checkDiff) {
        const diff = getSongIntensity(s, inst);
        const difficultyBucket = diff == null ? 0 : Math.max(1, Math.min(7, Math.trunc(diff) + 1));
        if (f.difficultyFilter[difficultyBucket] === false) return false;
      }
      return true;
    });

    const dir = sortAscending ? 1 : -1;
    return list.slice().sort((a, b) => {
      // eslint-disable-next-line no-useless-assignment
      let cmp = 0;
      switch (sortMode) {
        case 'title':
          cmp = a.title.localeCompare(b.title); break;
        case 'artist':
          cmp = a.artist.localeCompare(b.artist); break;
        case 'year':
          cmp = (a.year ?? 0) - (b.year ?? 0); break;
        case 'duration':
          cmp = (a.durationSeconds ?? 0) - (b.durationSeconds ?? 0); break;
        case 'intensity': {
          const ia = getSongIntensity(a, inst);
          const ib = getSongIntensity(b, inst);
          if (ia != null && ib != null) {
            cmp = ia - ib;
          } else if (ia != null) {
            cmp = -dir;
          } else if (ib != null) {
            cmp = dir;
          } else {
            cmp = a.title.localeCompare(b.title);
          }
          break;
        }
        /* v8 ignore start -- shop sort tiebreaker chain */
        case 'shop': {
          const aShop = shopSongIds?.has(a.songId) ? 1 : 0;
          const bShop = shopSongIds?.has(b.songId) ? 1 : 0;
          cmp = bShop - aShop; // Shop items first
          if (cmp === 0) {
            // Tiebreaker chain: if instrument filtered + has scores, use score comparison;
            // otherwise use title → artist → year
            if (inst && scoreMap.size > 0) {
              cmp = compareByMode('score', scoreMap.get(a.songId), scoreMap.get(b.songId));
            }
            if (cmp === 0) cmp = a.title.localeCompare(b.title);
            if (cmp === 0) cmp = a.artist.localeCompare(b.artist);
            if (cmp === 0) cmp = (a.year ?? 0) - (b.year ?? 0);
          }
          break;
        }
        /* v8 ignore stop */
        case 'maxdistance': {
          const sa = scoreMap.get(a.songId);
          const sb = scoreMap.get(b.songId);
          const ma = inst ? a.maxScores?.[inst] : undefined;
          const mb = inst ? b.maxScores?.[inst] : undefined;
          const ra = sa && sa.score > 0 && ma ? sa.score / ma : undefined;
          const rb = sb && sb.score > 0 && mb ? sb.score / mb : undefined;
          if (ra != null && rb != null) {
            cmp = ra - rb;
          } else if (ra != null) {
            cmp = -dir; // a scored, b didn't - scored wins regardless of sort direction
          } else if (rb != null) {
            cmp = dir;  // b scored, a didn't - scored wins regardless of sort direction
          } else {
            // Fallback: compare by raw score when max scores unavailable
            cmp = compareByMode('score', sa, sb);
          }
          break;
        }
        case 'maxscorediff': {
          const sa = scoreMap.get(a.songId);
          const sb = scoreMap.get(b.songId);
          const ma = inst ? a.maxScores?.[inst] : undefined;
          const mb = inst ? b.maxScores?.[inst] : undefined;
          const da = sa && sa.score > 0 && ma ? sa.score - ma : undefined;
          const db = sb && sb.score > 0 && mb ? sb.score - mb : undefined;
          if (da != null && db != null) {
            cmp = da - db;
          } else if (da != null) {
            cmp = -dir;
          } else if (db != null) {
            cmp = dir;
          } else {
            cmp = compareByMode('score', sa, sb);
          }
          break;
        }
        case 'lastplayed': {
          if (inst) {
            // Filtered: compare the single instrument's lastPlayedAt
            cmp = compareByMode('lastplayed', scoreMap.get(a.songId), scoreMap.get(b.songId));
          } else {
            // Unfiltered: find the most recent lastPlayedAt across all instruments
            const bestLp = (songId: string): string => {
              const byInst = allScoreMap.get(songId);
              if (!byInst) return '';
              let best = '';
              for (const sc of byInst.values()) {
                const lp = sc.validLastPlayedAt ?? sc.lastPlayedAt ?? '';
                if (lp > best) best = lp;
              }
              return best;
            };
            const la = bestLp(a.songId);
            const lb = bestLp(b.songId);
            if (la && !lb) cmp = -dir;
            else if (!la && lb) cmp = dir;
            else cmp = la.localeCompare(lb);
          }
          break;
        }
        default:
          if (scoreMap.size > 0) {
            cmp = compareByMode(sortMode, scoreMap.get(a.songId), scoreMap.get(b.songId));
          } else {
            cmp = a.title.localeCompare(b.title);
          }
      }
      return cmp === 0 ? a.title.localeCompare(b.title) * dir : cmp * dir;
    });
  }, [songs, search, sortMode, sortAscending, f, inst, scoreMap, allScoreMap, shopSongIds, isScoreValid, filterInvalidScoresEnabled]);
}
