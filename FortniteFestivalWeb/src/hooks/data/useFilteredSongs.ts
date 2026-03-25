/**
 * Hook that encapsulates the song list filtering and sorting logic.
 * Extracted from SongsPage for readability and potential reuse.
 */
import { useMemo } from 'react';
import { type ServerSong as Song, type PlayerScore, type ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';
import type { SongFilters, SongSortMode } from '../../utils/songSettings';
import { compareByMode } from '../../pages/songs/components/SongRow';

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
}

const PCT_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100];

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
    const activeFilterInstruments = inst
      ? (filterInstruments.has(inst) ? [inst] : [])
      : [...filterInstruments];

    // Pre-compute which filter checks are active
    const seasonKeys = Object.keys(f.seasonFilter);
    const checkSeason = hasPlayerData && seasonKeys.length > 0 && seasonKeys.some(k => f.seasonFilter[Number(k)] === false);
    const pctKeys = Object.keys(f.percentileFilter);
    const checkPct = hasPlayerData && pctKeys.length > 0 && pctKeys.some(k => f.percentileFilter[Number(k)] === false);
    const starKeys = Object.keys(f.starsFilter);
    const checkStars = hasPlayerData && starKeys.length > 0 && starKeys.some(k => f.starsFilter[Number(k)] === false);
    const diffKeys = Object.keys(f.difficultyFilter);
    const checkDiff = hasPlayerData && diffKeys.length > 0 && diffKeys.some(k => f.difficultyFilter[Number(k)] === false);

    const list = songs.filter(s => {
      if (q && !s.title.toLowerCase().includes(q) && !s.artist.toLowerCase().includes(q)) return false;
      if (!hasPlayerData) return true;

      const byInst = allScoreMap.get(s.songId);

      // Per-instrument filters
      if (activeFilterInstruments.length > 0) {
        let anyInstrumentPassed = false;
        for (const key of activeFilterInstruments) {
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
        // eslint-disable-next-line @typescript-eslint/no-explicit-any -- difficulty may be flat number from API
        const diff = (s as any).difficulty ?? 0;
        if (f.difficultyFilter[diff] === false) return false;
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
        default:
          if (scoreMap.size > 0) {
            cmp = compareByMode(sortMode, scoreMap.get(a.songId), scoreMap.get(b.songId));
          } else {
            cmp = a.title.localeCompare(b.title);
          }
      }
      return cmp === 0 ? a.title.localeCompare(b.title) * dir : cmp * dir;
    });
  }, [songs, search, sortMode, sortAscending, f, inst, scoreMap, allScoreMap, shopSongIds]);
}
