import type {InstrumentKey} from '../../core/instruments';
import type {LeaderboardData, ScoreTracker, Song} from '../../core/models';
import type {Settings} from '../../core/settings';

export type SongSortMode = 'title' | 'artist' | 'hasfc';

export type AdvancedMissingFilters = {
  missingPadFCs: boolean;
  missingProFCs: boolean;
  missingPadScores: boolean;
  missingProScores: boolean;
  includeLead: boolean;
  includeBass: boolean;
  includeDrums: boolean;
  includeVocals: boolean;
  includeProGuitar: boolean;
  includeProBass: boolean;
};

export const defaultAdvancedMissingFilters = (): AdvancedMissingFilters => ({
  missingPadFCs: false,
  missingProFCs: false,
  missingPadScores: false,
  missingProScores: false,
  includeLead: true,
  includeBass: true,
  includeDrums: true,
  includeVocals: true,
  includeProGuitar: true,
  includeProBass: true,
});

export type InstrumentOrderItem = {key: InstrumentKey; displayName: string};

export const defaultPrimaryInstrumentOrder = (): InstrumentOrderItem[] => [
  {key: 'guitar', displayName: 'Lead'},
  {key: 'drums', displayName: 'Drums'},
  {key: 'vocals', displayName: 'Vocals'},
  {key: 'bass', displayName: 'Bass'},
  {key: 'pro_guitar', displayName: 'Pro Guitar'},
  {key: 'pro_bass', displayName: 'Pro Bass'},
];

const canon = (s: string | undefined): string => (s ?? '').trim().toLowerCase();

const selectTracker = (ld: LeaderboardData, key: InstrumentKey): ScoreTracker | undefined => (ld as any)[key] as
  | ScoreTracker
  | undefined;

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
    if (filters.missingPadScores && (filters.includeLead || filters.includeBass || filters.includeDrums || filters.includeVocals))
      return true;
    if (filters.missingProScores && (filters.includeProGuitar || filters.includeProBass)) return true;
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

export const filterAndSortSongs = (params: {
  songs: ReadonlyArray<Song>;
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>;
  filterText?: string;
  advanced?: AdvancedMissingFilters;
  sortMode?: SongSortMode;
  sortAscending?: boolean;
  instrumentOrder?: ReadonlyArray<InstrumentOrderItem>;
}): Song[] => {
  const filterText = (params.filterText ?? '').trim();
  const sortMode = params.sortMode ?? 'title';
  const sortAscending = params.sortAscending ?? true;
  const advanced = params.advanced ?? defaultAdvancedMissingFilters();
  const instrumentOrder = params.instrumentOrder ?? defaultPrimaryInstrumentOrder();

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

  q.sort((a, b) => {
    if (sortMode === 'artist') {
      const aa = canon(a.track.an);
      const bb = canon(b.track.an);
      if (aa !== bb) return aa.localeCompare(bb);
      return canon(a.track.tt).localeCompare(canon(b.track.tt));
    }

    if (sortMode === 'hasfc') {
      const aPri = songHasAllFCsPriority(a.track.su, params.scoresIndex, instrumentOrder);
      const bPri = songHasAllFCsPriority(b.track.su, params.scoresIndex, instrumentOrder);
      if (aPri !== bPri) return bPri - aPri;

      const aSeq = songHasSequentialTopFCsScore(a.track.su, params.scoresIndex, instrumentOrder);
      const bSeq = songHasSequentialTopFCsScore(b.track.su, params.scoresIndex, instrumentOrder);
      if (aSeq !== bSeq) return bSeq - aSeq;

      return canon(a.track.tt).localeCompare(canon(b.track.tt));
    }

    // title
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

const trackerToStarString = (n: number): string => {
  if (n <= 0) return '';
  // Use a portable glyph; MAUI used '\u2605'.
  return '★'.repeat(Math.min(n, 6));
};

export const buildSongDisplayRow = (params: {
  song: Song;
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>;
  settings?: Settings;
}): SongDisplayRow => {
  const {song, scoresIndex, settings} = params;
  const id = song.track.su;

  const statuses: InstrumentStatus[] = [
    {instrumentKey: 'guitar', icon: 'guitar.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'drums', icon: 'drums.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'vocals', icon: 'vocals.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'bass', icon: 'bass.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'pro_guitar', icon: 'pro_guitar.png', hasScore: false, isFullCombo: false, isEnabled: true},
    {instrumentKey: 'pro_bass', icon: 'pro_bass.png', hasScore: false, isFullCombo: false, isEnabled: true},
  ];

  const ld = scoresIndex[id];

  let preferred: ScoreTracker | undefined;
  if (ld) preferred = ld.guitar ?? ld.drums ?? ld.vocals ?? ld.bass ?? ld.pro_guitar ?? ld.pro_bass;

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
      s.isEnabled =
        s.instrumentKey === 'guitar'
          ? settings.queryLead
          : s.instrumentKey === 'bass'
            ? settings.queryBass
            : s.instrumentKey === 'drums'
              ? settings.queryDrums
              : s.instrumentKey === 'vocals'
                ? settings.queryVocals
                : s.instrumentKey === 'pro_guitar'
                  ? settings.queryProLead
                  : s.instrumentKey === 'pro_bass'
                    ? settings.queryProBass
                    : true;
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
