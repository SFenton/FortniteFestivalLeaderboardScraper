import type {InstrumentKey} from '../instruments';
import type {LeaderboardData, ScoreTracker, Song} from '../models';
import type {Settings} from '../settings';

import type {AdvancedMissingFilters, InstrumentOrderItem, InstrumentShowSettings, MetadataSortKey, SongSortMode} from '../songListConfig';
import {defaultAdvancedMissingFilters, defaultMetadataSortPriority, defaultPrimaryInstrumentOrder, percentileBucket} from '../songListConfig';

// Re-export songListConfig types/values so existing consumers can import
// everything from @festival/core without changing their imports.
export type {AdvancedMissingFilters, InstrumentOrderItem, InstrumentShowSettings, MetadataSortItem, MetadataSortKey, SongSortMode} from '../songListConfig';
export {defaultAdvancedMissingFilters, defaultMetadataSortPriority, defaultPrimaryInstrumentOrder, instrumentSortModes, isInstrumentSortMode, isInstrumentVisible, normalizeInstrumentOrder, normalizeMetadataSortPriority, PERCENTILE_THRESHOLDS, percentileBucket, reorderPIOForVisibilityChange, showSettingKeyForInstrument} from '../songListConfig';

const canon = (s: string | undefined): string => (s ?? '').trim().toLowerCase();

const selectTracker = (ld: LeaderboardData, key: InstrumentKey): ScoreTracker | undefined => (ld as any)[key] as
  | ScoreTracker
  | undefined;

const fallbackDifficulty = (song: Song, key: InstrumentKey): number => {
  const i = song.track.in ?? {};
  switch (key) {
    case 'guitar':
      return i.gr ?? 0;
    case 'bass':
      return i.ba ?? 0;
    case 'drums':
      return i.ds ?? 0;
    case 'vocals':
      return i.vl ?? 0;
    case 'pro_guitar':
      return i.pg ?? i.gr ?? 0;
    case 'pro_bass':
      return i.pb ?? i.ba ?? 0;
    case 'peripheral_vocals':
      return i.vl ?? 0;
    case 'peripheral_cymbals':
      return i.ds ?? 0;
    case 'peripheral_drums':
      return i.ds ?? 0;
    default:
      return 0;
  }
};

const difficultyBucketForSong = (
  song: Song,
  entry: LeaderboardData | undefined,
  key: InstrumentKey,
): number => {
  const tracker = entry ? selectTracker(entry, key) : undefined;
  if (!tracker?.initialized) return 0;
  const raw = Number.isFinite(tracker.difficulty) ? Math.trunc(tracker.difficulty) : 0;
  const resolved = raw !== 0 ? raw : fallbackDifficulty(song, key);
  const clamped = Math.max(0, Math.min(6, resolved));
  return clamped + 1;
};

export const instrumentHasFC = (ld: LeaderboardData, key: InstrumentKey): boolean => {
  const tr = selectTracker(ld, key);
  return Boolean(tr && tr.initialized && tr.isFullCombo);
};

export const songHasAllFCsPriority = (
  songId: string,
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>,
  order: ReadonlyArray<InstrumentOrderItem>,
): number => {
  const entry = scoresIndex[songId];
  if (!entry) return 0;
  for (const inst of order) {
    if (!instrumentHasFC(entry, inst.key)) return 0;
  }
  return 1;
};

export const songHasSequentialTopFCsScore = (
  songId: string,
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>,
  order: ReadonlyArray<InstrumentOrderItem>,
): number => {
  const entry = scoresIndex[songId];
  if (!entry) return 0;
  let count = 0;
  for (const inst of order) {
    if (instrumentHasFC(entry, inst.key)) count++;
    else break;
  }
  return count;
};

const missingScore = (t: ScoreTracker | undefined): boolean => !t || !t.initialized;
const missingFC = (t: ScoreTracker | undefined): boolean => !t?.initialized || !t?.isFullCombo;

export const songMatchesAdvancedMissing = (
  song: Song,
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>,
  filters: AdvancedMissingFilters,
): boolean => {
  const songId = song.track.su;
  const entry = scoresIndex[songId];

  if (!entry) {
    if ((filters.missingPadScores || filters.missingPadFCs) && (filters.includeLead || filters.includeBass || filters.includeDrums || filters.includeVocals))
      return true;
    if ((filters.missingProScores || filters.missingProFCs) && (filters.includeProGuitar || filters.includeProBass)) return true;
    return false;
  }

  let match = false;

  if (filters.includeLead) {
    if (filters.missingPadScores && missingScore(entry.guitar)) match = true;
    if (filters.missingPadFCs && missingFC(entry.guitar)) match = true;
  }
  if (filters.includeDrums) {
    if (filters.missingPadScores && missingScore(entry.drums)) match = true;
    if (filters.missingPadFCs && missingFC(entry.drums)) match = true;
  }
  if (filters.includeVocals) {
    if (filters.missingPadScores && missingScore(entry.vocals)) match = true;
    if (filters.missingPadFCs && missingFC(entry.vocals)) match = true;
  }
  if (filters.includeBass) {
    if (filters.missingPadScores && missingScore(entry.bass)) match = true;
    if (filters.missingPadFCs && missingFC(entry.bass)) match = true;
  }

  if (filters.includeProGuitar) {
    if (filters.missingProScores && missingScore(entry.pro_guitar)) match = true;
    if (filters.missingProFCs && missingFC(entry.pro_guitar)) match = true;
  }
  if (filters.includeProBass) {
    if (filters.missingProScores && missingScore(entry.pro_bass)) match = true;
    if (filters.missingProFCs && missingFC(entry.pro_bass)) match = true;
  }

  return match;
};

/** Compare two songs by a single metadata key. Returns <0, 0, or >0. */
const compareByMetadataKey = (
  key: MetadataSortKey,
  a: Song,
  b: Song,
  aTracker: ScoreTracker | undefined,
  bTracker: ScoreTracker | undefined,
): number => {
  switch (key) {
    case 'title':
      return canon(a.track.tt).localeCompare(canon(b.track.tt));
    case 'artist':
      return canon(a.track.an).localeCompare(canon(b.track.an));
    case 'year': {
      const aVal = a.track.ry ?? 0;
      const bVal = b.track.ry ?? 0;
      return aVal - bVal;
    }
    case 'score': {
      const aVal = aTracker?.initialized ? aTracker.maxScore : 0;
      const bVal = bTracker?.initialized ? bTracker.maxScore : 0;
      return aVal - bVal;
    }
    case 'percentage': {
      const aVal = aTracker?.initialized ? aTracker.percentHit : 0;
      const bVal = bTracker?.initialized ? bTracker.percentHit : 0;
      return aVal - bVal;
    }
    case 'percentile': {
      const aVal = aTracker?.initialized ? aTracker.rawPercentile : 0;
      const bVal = bTracker?.initialized ? bTracker.rawPercentile : 0;
      return aVal - bVal;
    }
    case 'isfc': {
      const aVal = aTracker?.initialized && aTracker.isFullCombo ? 1 : 0;
      const bVal = bTracker?.initialized && bTracker.isFullCombo ? 1 : 0;
      return aVal - bVal;
    }
    case 'stars': {
      const aVal = aTracker?.initialized ? aTracker.numStars : 0;
      const bVal = bTracker?.initialized ? bTracker.numStars : 0;
      return aVal - bVal;
    }
    case 'seasonachieved': {
      const aVal = aTracker?.initialized ? aTracker.seasonAchieved : 0;
      const bVal = bTracker?.initialized ? bTracker.seasonAchieved : 0;
      return aVal - bVal;
    }
    case 'intensity': {
      const aRaw = aTracker?.initialized ? aTracker.difficulty : -1;
      const bRaw = bTracker?.initialized ? bTracker.difficulty : -1;
      const aVal = aRaw >= 0 ? Math.max(0, Math.min(6, Math.trunc(aRaw))) + 1 : 0;
      const bVal = bRaw >= 0 ? Math.max(0, Math.min(6, Math.trunc(bRaw))) + 1 : 0;
      return aVal - bVal;
    }
    default:
      return 0;
  }
};

export const filterAndSortSongs = (params: {
  songs: ReadonlyArray<Song>;
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>;
  filterText?: string;
  advanced?: AdvancedMissingFilters;
  sortMode?: SongSortMode;
  sortAscending?: boolean;
  instrumentOrder?: ReadonlyArray<InstrumentOrderItem>;
  instrumentFilter?: InstrumentKey | null;
  metadataSortPriority?: ReadonlyArray<MetadataSortKey>;
}): Song[] => {
  const filterText = (params.filterText ?? '').trim();
  const sortMode = params.sortMode ?? 'title';
  const sortAscending = params.sortAscending ?? true;
  const advanced = params.advanced ?? defaultAdvancedMissingFilters();
  const instrumentOrder = params.instrumentOrder ?? defaultPrimaryInstrumentOrder();
  const instrumentFilter = params.instrumentFilter ?? null;
  const metadataPriority: ReadonlyArray<MetadataSortKey> = params.metadataSortPriority ?? defaultMetadataSortPriority().map(i => i.key);

  let q = [...params.songs];

  if (filterText) {
    const low = filterText.toLowerCase();
    q = q.filter(
      s => canon(s.track.tt).includes(low) || canon(s.track.an).includes(low),
    );
  }

  const anyMissing =
    advanced.missingPadFCs || advanced.missingProFCs || advanced.missingPadScores || advanced.missingProScores;
  if (anyMissing) {
    q = q.filter(s => songMatchesAdvancedMissing(s, params.scoresIndex, advanced));
  }

  // Season filter – only applies when an instrument is selected and at least one
  // season has been explicitly toggled off (empty record = all shown).
  const sf = advanced.seasonFilter ?? {};
  const hasSeasonFilter = instrumentFilter != null && Object.values(sf).some(v => v === false);
  if (hasSeasonFilter) {
    q = q.filter(s => {
      const entry = params.scoresIndex[s.track.su];
      const tracker = entry ? selectTracker(entry, instrumentFilter!) : undefined;
      const season = tracker?.initialized ? tracker.seasonAchieved : 0;
      // If the season is explicitly false, exclude the song; otherwise include it.
      return sf[season] !== false;
    });
  }

  // Percentile filter – same pattern as season filter.
  const pf = advanced.percentileFilter ?? {};
  const hasPercentileFilter = instrumentFilter != null && Object.values(pf).some(v => v === false);
  if (hasPercentileFilter) {
    q = q.filter(s => {
      const entry = params.scoresIndex[s.track.su];
      const tracker = entry ? selectTracker(entry, instrumentFilter!) : undefined;
      const bucket = tracker?.initialized ? percentileBucket(tracker.rawPercentile) : 0;
      return pf[bucket] !== false;
    });
  }

  // Stars filter – same pattern as season/percentile filter.
  const stf = advanced.starsFilter ?? {};
  const hasStarsFilter = instrumentFilter != null && Object.values(stf).some(v => v === false);
  if (hasStarsFilter) {
    q = q.filter(s => {
      const entry = params.scoresIndex[s.track.su];
      const tracker = entry ? selectTracker(entry, instrumentFilter!) : undefined;
      const stars = tracker?.initialized ? Math.min(tracker.numStars, 6) : 0;
      return stf[stars] !== false;
    });
  }

  // Difficulty filter – same pattern as season/percentile/stars filter.
  const df = advanced.difficultyFilter ?? {};
  const hasDifficultyFilter = instrumentFilter != null && Object.values(df).some(v => v === false);
  if (hasDifficultyFilter) {
    q = q.filter(s => {
      const entry = params.scoresIndex[s.track.su];
      const difficultyBucket = difficultyBucketForSong(s, entry, instrumentFilter!);
      return df[difficultyBucket] !== false;
    });
  }

  q.sort((a, b) => {
    if (sortMode === 'year') {
      const ay = a.track.ry ?? 0;
      const by = b.track.ry ?? 0;
      if (ay !== by) return ay - by;
      const at = canon(a.track.tt);
      const bt = canon(b.track.tt);
      if (at !== bt) return at.localeCompare(bt);
      return canon(a.track.an).localeCompare(canon(b.track.an));
    }

    if (sortMode === 'artist') {
      const aa = canon(a.track.an);
      const bb = canon(b.track.an);
      if (aa !== bb) return aa.localeCompare(bb);
      const ay = a.track.ry ?? 0;
      const by = b.track.ry ?? 0;
      if (ay !== by) return ay - by;
      return canon(a.track.tt).localeCompare(canon(b.track.tt));
    }

    if (sortMode === 'hasfc') {
      const aPri = songHasAllFCsPriority(a.track.su, params.scoresIndex, instrumentOrder);
      const bPri = songHasAllFCsPriority(b.track.su, params.scoresIndex, instrumentOrder);
      if (aPri !== bPri) return bPri - aPri;

      const aSeq = songHasSequentialTopFCsScore(a.track.su, params.scoresIndex, instrumentOrder);
      const bSeq = songHasSequentialTopFCsScore(b.track.su, params.scoresIndex, instrumentOrder);
      if (aSeq !== bSeq) return bSeq - aSeq;

      const ay = a.track.ry ?? 0;
      const by = b.track.ry ?? 0;
      if (ay !== by) return ay - by;
      const at2 = canon(a.track.tt);
      const bt2 = canon(b.track.tt);
      if (at2 !== bt2) return at2.localeCompare(bt2);
      return canon(a.track.an).localeCompare(canon(b.track.an));
    }

    // Instrument-specific sort modes (require instrumentFilter)
    if (instrumentFilter && (sortMode === 'isfc' || sortMode === 'score' || sortMode === 'percentage' || sortMode === 'percentile' || sortMode === 'stars' || sortMode === 'seasonachieved' || sortMode === 'intensity')) {
      const aEntry = params.scoresIndex[a.track.su];
      const bEntry = params.scoresIndex[b.track.su];
      const aTracker = aEntry ? selectTracker(aEntry, instrumentFilter) : undefined;
      const bTracker = bEntry ? selectTracker(bEntry, instrumentFilter) : undefined;

      // Primary sort by the selected mode
      const primary = compareByMetadataKey(sortMode, a, b, aTracker, bTracker);
      if (primary !== 0) return primary;

      // Cascade through metadata priority order, skipping the active sort
      for (const key of metadataPriority) {
        if (key === sortMode) continue;
        const c = compareByMetadataKey(key, a, b, aTracker, bTracker);
        if (c !== 0) return c;
      }
      return 0;
    }

    // title: sort by Title then Year then Artist
    if (sortMode === 'title') {
      const at = canon(a.track.tt);
      const bt = canon(b.track.tt);
      if (at !== bt) return at.localeCompare(bt);
      const ay = a.track.ry ?? 0;
      const by = b.track.ry ?? 0;
      if (ay !== by) return ay - by;
      return canon(a.track.an).localeCompare(canon(b.track.an));
    }

    // default fallback: year then title then artist
    const ayr = a.track.ry ?? 0;
    const byr = b.track.ry ?? 0;
    if (ayr !== byr) return ayr - byr;
    const at = canon(a.track.tt);
    const bt = canon(b.track.tt);
    if (at !== bt) return at.localeCompare(bt);
    return canon(a.track.an).localeCompare(canon(b.track.an));
  });

  if (!sortAscending) q.reverse();

  return q;
};

export type InstrumentStatus = {
  instrumentKey: InstrumentKey;
  icon: string;
  hasScore: boolean;
  isFullCombo: boolean;
  isEnabled: boolean;
};

export type SongDisplayRow = {
  songId: string;
  title: string;
  artist: string;
  releaseYear?: number;
  imagePath?: string;
  isSelected: boolean;
  score: number;
  stars: string;
  isFullCombo: boolean;
  percentHitRaw: number;
  season: string;
  instrumentStatuses: InstrumentStatus[];
};

export type InstrumentQuerySettings = Pick<
  Settings,
  'queryLead' | 'queryBass' | 'queryDrums' | 'queryVocals' | 'queryProLead' | 'queryProBass'
>;

const trackerToStarString = (n: number): string => {
  if (n <= 0) return '';
  // Use a portable glyph; MAUI used '\u2605'.
  return '★'.repeat(Math.min(n, 6));
};

export const buildSongDisplayRow = (params: {
  song: Song;
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>;
  settings?: InstrumentShowSettings;
} | {
  song: Song;
  leaderboardData?: LeaderboardData;
  settings?: InstrumentShowSettings;
}): SongDisplayRow => {
  const {song, settings} = params;
  const id = song.track.su;

  const statuses: InstrumentStatus[] = [
    {instrumentKey: 'guitar', icon: 'guitar.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'drums', icon: 'drums.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'vocals', icon: 'vocals.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'bass', icon: 'bass.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'pro_guitar', icon: 'pro_guitar.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'pro_bass', icon: 'pro_bass.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'peripheral_vocals', icon: 'peripheral_vocals.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'peripheral_cymbals', icon: 'peripheral_cymbals.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'peripheral_drums', icon: 'peripheral_drums.png', hasScore: false, isFullCombo: false, isEnabled: true},
  ];

  const ld = 'scoresIndex' in params ? params.scoresIndex[id] : params.leaderboardData;

  let preferred: ScoreTracker | undefined;
  if (ld) preferred = ld.guitar ?? ld.drums ?? ld.vocals ?? ld.bass ?? ld.pro_guitar ?? ld.pro_bass ?? ld.peripheral_vocals ?? ld.peripheral_cymbals ?? ld.peripheral_drums;

  const base = {
    songId: id,
    title: song.track.tt ?? song._title ?? '',
    artist: song.track.an ?? '',
    releaseYear: song.track.ry,
    imagePath: song.imagePath,
    isSelected: Boolean(song.isSelected),
  };

  const score = preferred?.initialized ? preferred.maxScore : 0;
  const stars = preferred?.initialized ? trackerToStarString(preferred.numStars) : '';
  const isFullCombo = Boolean(preferred?.initialized && preferred.isFullCombo);
  const percentHitRaw = preferred?.initialized ? preferred.percentHit : 0;
  const season = preferred?.initialized ? (preferred.seasonAchieved !== 0 ? String(preferred.seasonAchieved) : '-1') : '';

  // Update statuses
  if (!ld) {
    for (const s of statuses) {
      s.hasScore = false;
      s.isFullCombo = false;
    }
  } else {
    for (const s of statuses) {
      const tr = selectTracker(ld, s.instrumentKey);
      if (!tr || !tr.initialized) {
        s.hasScore = false;
        s.isFullCombo = false;
      } else {
        s.hasScore = true;
        s.isFullCombo = Boolean(tr.isFullCombo);
      }
    }
  }

  // Apply settings enable flags
  if (settings) {
    for (const s of statuses) {
      switch (s.instrumentKey) {
        case 'guitar': s.isEnabled = settings.showLead ?? true; break;
        case 'bass': s.isEnabled = settings.showBass ?? true; break;
        case 'drums': s.isEnabled = settings.showDrums ?? true; break;
        case 'vocals': s.isEnabled = settings.showVocals ?? true; break;
        case 'pro_guitar': s.isEnabled = settings.showProLead ?? true; break;
        case 'pro_bass': s.isEnabled = settings.showProBass ?? true; break;
        case 'peripheral_vocals': s.isEnabled = settings.showPeripheralVocals ?? true; break;
        case 'peripheral_cymbals': s.isEnabled = settings.showPeripheralCymbals ?? true; break;
        case 'peripheral_drums': s.isEnabled = settings.showPeripheralDrums ?? true; break;
        default: s.isEnabled = true;
      }
    }
  }

  return {
    ...base,
    score,
    stars,
    isFullCombo,
    percentHitRaw,
    season,
    instrumentStatuses: statuses,
  };
};
