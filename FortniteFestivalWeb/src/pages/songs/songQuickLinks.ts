import { formatPercentileBucket } from '@festival/core';
import { DEFAULT_INSTRUMENT, type PlayerScore, type ServerInstrumentKey as InstrumentKey, type ServerSong as Song } from '@festival/core/api/serverTypes';
import type { TFunction } from 'i18next';
import type { SongSortMode } from '../../utils/songSettings';
import { getSongInstrumentDifficulty } from '../../utils/songInstrumentDifficulty';

const PERCENTILE_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;

export type SongQuickLinkSection = {
  id: string;
  label: string;
  shortLabel: string;
  landmarkLabel: string;
  rowIndex: number;
  songs: Song[];
};

export type SongVirtualRow =
  | { type: 'section'; section: SongQuickLinkSection }
  | { type: 'song'; song: Song; sectionId: string | null; songIndex: number };

type BuildSongQuickLinkSectionsOptions = {
  songs: readonly Song[];
  sortMode: SongSortMode;
  instrument: InstrumentKey | null;
  scoreMap: ReadonlyMap<string, PlayerScore>;
  allScoreMap: ReadonlyMap<string, ReadonlyMap<InstrumentKey, PlayerScore>>;
  shopSongIds?: ReadonlySet<string> | null;
  leavingTomorrowIds?: ReadonlySet<string> | null;
  t: TFunction;
};

type SectionBucket = {
  id: string;
  label: string;
  shortLabel: string;
  landmarkLabel: string;
};

export function buildSongQuickLinkSections({
  songs,
  sortMode,
  instrument,
  scoreMap,
  allScoreMap,
  shopSongIds,
  leavingTomorrowIds,
  t,
}: BuildSongQuickLinkSectionsOptions): { sections: SongQuickLinkSection[]; rows: SongVirtualRow[] } {
  if (songs.length === 0) {
    return { sections: [], rows: [] };
  }

  const bucketOrder: SongQuickLinkSection[] = [];
  const sectionsById = new Map<string, SongQuickLinkSection>();

  for (const song of songs) {
    const bucket = getSongQuickLinkBucket({
      song,
      sortMode,
      instrument,
      scoreMap,
      allScoreMap,
      shopSongIds,
      leavingTomorrowIds,
      t,
    });

    let section = sectionsById.get(bucket.id);
    if (!section) {
      section = {
        id: bucket.id,
        label: bucket.label,
        shortLabel: bucket.shortLabel,
        landmarkLabel: bucket.landmarkLabel,
        rowIndex: 0,
        songs: [],
      };
      sectionsById.set(bucket.id, section);
      bucketOrder.push(section);
    }

    section.songs.push(song);
  }

  const shouldRenderSections = bucketOrder.length >= 2;
  if (!shouldRenderSections) {
    return {
      sections: bucketOrder,
      rows: songs.map((song, songIndex) => ({ type: 'song', song, sectionId: bucketOrder[0]?.id ?? null, songIndex })),
    };
  }

  const rows: SongVirtualRow[] = [];
  let rowIndex = 0;
  let songIndex = 0;
  for (const section of bucketOrder) {
    section.rowIndex = rowIndex;
    rows.push({ type: 'section', section });
    rowIndex += 1;
    for (const song of section.songs) {
      rows.push({ type: 'song', song, sectionId: section.id, songIndex });
      rowIndex += 1;
      songIndex += 1;
    }
  }

  return {
    sections: bucketOrder,
    rows,
  };
}

function getSongQuickLinkBucket({
  song,
  sortMode,
  instrument,
  scoreMap,
  allScoreMap,
  shopSongIds,
  leavingTomorrowIds,
  t,
}: BuildSongQuickLinkSectionsOptions & { song: Song }): SectionBucket {
  switch (sortMode) {
    case 'title':
      return getAlphaBucket(sortMode, song.title, t);
    case 'artist':
      return getAlphaBucket(sortMode, song.artist, t);
    case 'year':
      return getYearBucket(sortMode, song.year, t);
    case 'duration':
      return getDurationBucket(sortMode, song.durationSeconds, t);
    case 'shop':
      return getShopBucket(sortMode, song.songId, shopSongIds, leavingTomorrowIds, t);
    case 'hasfc':
      return getHasFcBucket(sortMode, scoreMap.get(song.songId), t);
    case 'lastplayed':
      return getLastPlayedBucket(sortMode, song.songId, instrument, scoreMap, allScoreMap, t);
    case 'score':
      return getScoreBucket(sortMode, scoreMap.get(song.songId)?.score, t);
    case 'percentage':
      return getPercentageBucket(sortMode, scoreMap.get(song.songId)?.accuracy, t);
    case 'percentile':
      return getPercentileBucket(sortMode, scoreMap.get(song.songId), t);
    case 'stars':
      return getStarsBucket(sortMode, scoreMap.get(song.songId)?.stars, t);
    case 'seasonachieved':
      return getSeasonBucket(sortMode, scoreMap.get(song.songId)?.season, t);
    case 'intensity':
      return getIntensityBucket(sortMode, song, instrument, t);
    case 'difficulty':
      return getDifficultyBucket(sortMode, scoreMap.get(song.songId), t);
    case 'maxdistance':
      return getMaxDistanceBucket(sortMode, scoreMap.get(song.songId), song, instrument, t);
    case 'maxscorediff':
      return getMaxScoreDiffBucket(sortMode, scoreMap.get(song.songId), song, instrument, t);
    default:
      return getAlphaBucket('title', song.title, t);
  }
}

function getAlphaBucket(sortMode: SongSortMode, value: string, t: TFunction): SectionBucket {
  const normalized = value.trim().charAt(0).toUpperCase();
  const token = /^[A-Z]$/.test(normalized) ? normalized : '#';
  return {
    id: `${sortMode}:${token.toLowerCase()}`,
    label: token === '#' ? t('songs.quickLinks.numeric') : token,
    shortLabel: token,
    landmarkLabel: token === '#' ? t('songs.quickLinks.numeric') : token,
  };
}

function getYearBucket(sortMode: SongSortMode, year: number | undefined, t: TFunction): SectionBucket {
  if (!year || year <= 0) {
    return makeBucket(sortMode, 'unknown', t('songs.quickLinks.unknownYear'), t('songs.quickLinks.unknownShort'));
  }

  const decade = Math.floor(year / 10) * 10;
  return makeBucket(sortMode, String(decade), `${decade}s`, `${String(decade).slice(2)}s`);
}

function getDurationBucket(sortMode: SongSortMode, durationSeconds: number | undefined, t: TFunction): SectionBucket {
  if (!durationSeconds || durationSeconds <= 0) {
    return makeBucket(sortMode, 'unknown', t('songs.quickLinks.unknownDuration'), t('songs.quickLinks.unknownShort'));
  }

  if (durationSeconds < 120) return makeBucket(sortMode, 'lt2', '<2m', '<2');
  if (durationSeconds < 180) return makeBucket(sortMode, '2to3', '2-3m', '2-3');
  if (durationSeconds < 240) return makeBucket(sortMode, '3to4', '3-4m', '3-4');
  if (durationSeconds < 300) return makeBucket(sortMode, '4to5', '4-5m', '4-5');
  return makeBucket(sortMode, 'gte5', '5m+', '5+');
}

function getShopBucket(
  sortMode: SongSortMode,
  songId: string,
  shopSongIds: ReadonlySet<string> | null | undefined,
  leavingTomorrowIds: ReadonlySet<string> | null | undefined,
  t: TFunction,
): SectionBucket {
  if (leavingTomorrowIds?.has(songId)) {
    return makeBucket(sortMode, 'leaving-tomorrow', t('songs.quickLinks.leavingTomorrow'), t('songs.quickLinks.leavingTomorrowShort'));
  }
  if (shopSongIds?.has(songId)) {
    return makeBucket(sortMode, 'in-shop', t('songs.quickLinks.inShop'), t('songs.quickLinks.inShopShort'));
  }
  return makeBucket(sortMode, 'not-in-shop', t('songs.quickLinks.notInShop'), t('songs.quickLinks.notInShopShort'));
}

function getHasFcBucket(sortMode: SongSortMode, score: PlayerScore | undefined, t: TFunction): SectionBucket {
  if (!score || score.score <= 0) {
    return makeBucket(sortMode, 'no-score', t('filter.noScore'), t('songs.quickLinks.noScoreShort'));
  }
  if (score.isFullCombo) {
    return makeBucket(sortMode, 'fc', t('songs.quickLinks.fc'), t('songs.quickLinks.fcShort'));
  }
  return makeBucket(sortMode, 'no-fc', t('songs.quickLinks.noFc'), t('songs.quickLinks.noFcShort'));
}

function getLastPlayedBucket(
  sortMode: SongSortMode,
  songId: string,
  instrument: InstrumentKey | null,
  scoreMap: ReadonlyMap<string, PlayerScore>,
  allScoreMap: ReadonlyMap<string, ReadonlyMap<InstrumentKey, PlayerScore>>,
  t: TFunction,
): SectionBucket {
  const lastPlayed = instrument
    ? scoreMap.get(songId)?.validLastPlayedAt ?? scoreMap.get(songId)?.lastPlayedAt
    : getBestLastPlayed(allScoreMap.get(songId));

  if (!lastPlayed) {
    return makeBucket(sortMode, 'never', t('songs.quickLinks.neverPlayed'), t('songs.quickLinks.neverPlayedShort'));
  }

  const value = new Date(lastPlayed);
  if (Number.isNaN(value.getTime())) {
    return makeBucket(sortMode, 'older', t('songs.quickLinks.older'), t('songs.quickLinks.olderShort'));
  }

  const now = new Date();
  const diffMs = now.getTime() - value.getTime();
  const diffDays = diffMs / (1000 * 60 * 60 * 24);
  if (diffDays < 1) return makeBucket(sortMode, 'today', t('songs.quickLinks.today'), t('songs.quickLinks.todayShort'));
  if (diffDays < 7) return makeBucket(sortMode, 'week', t('songs.quickLinks.thisWeek'), t('songs.quickLinks.thisWeekShort'));
  if (diffDays < 31) return makeBucket(sortMode, 'month', t('songs.quickLinks.thisMonth'), t('songs.quickLinks.thisMonthShort'));
  if (diffDays < 366) return makeBucket(sortMode, 'year', t('songs.quickLinks.thisYear'), t('songs.quickLinks.thisYearShort'));
  return makeBucket(sortMode, 'older', t('songs.quickLinks.older'), t('songs.quickLinks.olderShort'));
}

function getScoreBucket(sortMode: SongSortMode, score: number | undefined, t: TFunction): SectionBucket {
  if (!score || score <= 0) {
    return makeBucket(sortMode, 'no-score', t('filter.noScore'), t('songs.quickLinks.noScoreShort'));
  }

  const step = score >= 1_000_000 ? 100_000 : 50_000;
  const floor = Math.floor(score / step) * step;
  const label = `${formatCompactNumber(floor)}+`;
  return makeBucket(sortMode, String(floor), label, label);
}

function getPercentageBucket(sortMode: SongSortMode, accuracy: number | undefined, t: TFunction): SectionBucket {
  if (accuracy == null || accuracy <= 0) {
    return makeBucket(sortMode, 'no-score', t('filter.noScore'), t('songs.quickLinks.noScoreShort'));
  }

  if (accuracy >= 100) return makeBucket(sortMode, '100', '100%', '100');
  if (accuracy >= 99) return makeBucket(sortMode, '99', '99%', '99');
  if (accuracy >= 98) return makeBucket(sortMode, '98', '98%', '98');
  if (accuracy >= 95) return makeBucket(sortMode, '95', '95-97%', '95+');
  if (accuracy >= 90) return makeBucket(sortMode, '90', '90-94%', '90+');
  return makeBucket(sortMode, 'lt90', '<90%', '<90');
}

function getPercentileBucket(sortMode: SongSortMode, score: PlayerScore | undefined, t: TFunction): SectionBucket {
  if (!score || score.rank <= 0 || (score.totalEntries ?? 0) <= 0) {
    return makeBucket(sortMode, 'no-rank', t('songs.quickLinks.noRank'), t('songs.quickLinks.noRankShort'));
  }

  const percentile = Math.min((score.rank / score.totalEntries!) * 100, 100);
  const bucket = PERCENTILE_THRESHOLDS.find((threshold) => percentile <= threshold) ?? 100;
    const display = formatPercentileBucket(bucket).replace(/^Top\s+/, '');
    return makeBucket(sortMode, String(bucket), display, `${bucket}%`);
}

function getStarsBucket(sortMode: SongSortMode, stars: number | undefined, t: TFunction): SectionBucket {
  if (stars == null || stars <= 0) {
    return makeBucket(sortMode, 'no-score', t('filter.noScore'), t('songs.quickLinks.noScoreShort'));
  }

  return makeBucket(sortMode, String(stars), `${stars}★`, `${stars}★`);
}

function getSeasonBucket(sortMode: SongSortMode, season: number | undefined, t: TFunction): SectionBucket {
  if (!season || season <= 0) {
    return makeBucket(sortMode, 'no-season', t('songs.quickLinks.noSeason'), t('songs.quickLinks.noSeasonShort'));
  }

  return makeBucket(sortMode, `s${season}`, `S${season}`, `S${season}`);
}

function getIntensityBucket(sortMode: SongSortMode, song: Song, instrument: InstrumentKey | null, t: TFunction): SectionBucket {
  const intensity = getSongInstrumentDifficulty(song, instrument ?? DEFAULT_INSTRUMENT);
  if (intensity == null || intensity < 0) {
    return makeBucket(sortMode, 'unknown', t('songs.quickLinks.unknownIntensity'), t('songs.quickLinks.unknownShort'));
  }

  const display = intensity + 1;
  return {
    id: `${sortMode}:${intensity}`,
    label: `${display}`,
    shortLabel: `${display}`,
    landmarkLabel: `Difficulty ${display} of 7`,
  };
}

function getDifficultyBucket(sortMode: SongSortMode, score: PlayerScore | undefined, t: TFunction): SectionBucket {
  if (!score || score.score <= 0) {
    return makeBucket(sortMode, 'no-score', t('filter.noScore'), t('songs.quickLinks.noScoreShort'));
  }

  const difficulty = score.difficulty;
  if (difficulty == null || difficulty < 0) {
    return makeBucket(sortMode, 'unknown', t('songs.quickLinks.unknownDifficulty'), t('songs.quickLinks.unknownShort'));
  }

  const label = difficulty === 0
    ? t('paths.easy')
    : difficulty === 1
      ? t('paths.medium')
      : difficulty === 2
        ? t('paths.hard')
        : difficulty === 3
          ? t('paths.expert')
          : `${difficulty}`;

  return makeBucket(sortMode, String(difficulty), label, label);
}

function getMaxDistanceBucket(
  sortMode: SongSortMode,
  score: PlayerScore | undefined,
  song: Song,
  instrument: InstrumentKey | null,
  t: TFunction,
): SectionBucket {
  const ratio = getMaxScoreRatio(score, song, instrument);
  if (ratio == null) {
    return makeBucket(sortMode, 'no-score', t('filter.noScore'), t('songs.quickLinks.noScoreShort'));
  }

  if (ratio >= 1) return makeBucket(sortMode, '100', '100%', '100');
  if (ratio >= 0.99) return makeBucket(sortMode, '99', '99%+', '99+');
  if (ratio >= 0.98) return makeBucket(sortMode, '98', '98%+', '98+');
  if (ratio >= 0.95) return makeBucket(sortMode, '95', '95%+', '95+');
  if (ratio >= 0.9) return makeBucket(sortMode, '90', '90%+', '90+');
  return makeBucket(sortMode, 'lt90', '<90%', '<90');
}

function getMaxScoreDiffBucket(
  sortMode: SongSortMode,
  score: PlayerScore | undefined,
  song: Song,
  instrument: InstrumentKey | null,
  t: TFunction,
): SectionBucket {
  const maxScore = getMaxScore(song, instrument);
  if (!score || score.score <= 0 || maxScore == null) {
    return makeBucket(sortMode, 'no-score', t('filter.noScore'), t('songs.quickLinks.noScoreShort'));
  }

  const diff = score.score - maxScore;
  if (diff >= 0) return makeBucket(sortMode, 'max', t('songs.quickLinks.atMax'), t('songs.quickLinks.atMaxShort'));
  if (diff >= -1000) return makeBucket(sortMode, 'lt1k', '<1k', '<1k');
  if (diff >= -5000) return makeBucket(sortMode, 'lt5k', '<5k', '<5k');
  if (diff >= -10000) return makeBucket(sortMode, 'lt10k', '<10k', '<10k');
  if (diff >= -25000) return makeBucket(sortMode, 'lt25k', '<25k', '<25k');
  if (diff >= -50000) return makeBucket(sortMode, 'lt50k', '<50k', '<50k');
  return makeBucket(sortMode, 'gte50k', '50k+', '50k+');
}

function makeBucket(sortMode: SongSortMode, token: string, label: string, shortLabel: string): SectionBucket {
  return {
    id: `${sortMode}:${token}`,
    label,
    shortLabel,
    landmarkLabel: label,
  };
}

function getBestLastPlayed(byInstrument: ReadonlyMap<InstrumentKey, PlayerScore> | undefined): string {
  if (!byInstrument) return '';
  let best = '';
  for (const score of byInstrument.values()) {
    const candidate = score.validLastPlayedAt ?? score.lastPlayedAt ?? '';
    if (candidate > best) {
      best = candidate;
    }
  }
  return best;
}

function getMaxScoreRatio(score: PlayerScore | undefined, song: Song, instrument: InstrumentKey | null): number | null {
  const maxScore = getMaxScore(song, instrument);
  if (!score || score.score <= 0 || maxScore == null || maxScore <= 0) {
    return null;
  }
  return score.score / maxScore;
}

function getMaxScore(song: Song, instrument: InstrumentKey | null): number | undefined {
  if (!instrument) return undefined;
  return song.maxScores?.[instrument];
}

function formatCompactNumber(value: number): string {
  if (value >= 1_000_000) {
    const millions = value / 1_000_000;
    return Number.isInteger(millions) ? `${millions}M` : `${millions.toFixed(1)}M`;
  }
  if (value >= 1_000) {
    return `${Math.round(value / 1_000)}k`;
  }
  return `${value}`;
}