import type {InstrumentKey} from '../instruments';
import type {LeaderboardData, ScoreTracker, Song} from '../models';
import {getSongInstrumentDifficulty, songSupportsInstrument} from '../songAvailability';
import {formatIntegerWithCommas} from './formatters';

export type SongInfoInstrumentRow = {
  key: InstrumentKey;
  name: string;
  icon: string;
  hasScore: boolean;
  isFullCombo: boolean;
  starsCount: number;
  gameDifficultyDisplay?: string;
  scoreDisplay: string;
  percentDisplay: string;
  seasonDisplay: string;
  percentileDisplay: string;
  rankDisplay: string;
  totalEntriesDisplay: string;
  rankOutOfDisplay: string;
  isTop5Percentile: boolean;
  rawDifficulty: number;
};

const keyToDisplayName = (key: InstrumentKey): string => {
  switch (key) {
    case 'guitar':
      return 'Lead';
    case 'drums':
      return 'Drums';
    case 'vocals':
      return 'Tap Vocals';
    case 'bass':
      return 'Bass';
    case 'pro_guitar':
      return 'Pro Lead';
    case 'pro_bass':
      return 'Pro Bass';
    case 'peripheral_vocals':
      return 'Mic Mode';
    case 'peripheral_cymbals':
      return 'Pro Drums + Cymbals';
    case 'peripheral_drums':
      return 'Pro Drums';
    default:
      return key;
  }
};

export const formatPercent = (raw: number): string => {
  if (raw <= 0) return '0%';
  let value = raw / 10000;
  if (value > 100) value = 100;

  if (Number.isInteger(value)) return `${value}%`;
  if (Number.isInteger(value * 10)) return `${value.toFixed(1)}%`;
  return `${value.toFixed(2)}%`;
};

export const formatSeason = (seasonAchieved: number): string => {
  if (seasonAchieved <= 0) return 'All-Time';
  return `S${seasonAchieved}`;
};

export const composeRankOutOf = (rankDisplay: string, totalEntriesDisplay: string): string => {
  const hasRank = Boolean(rankDisplay && rankDisplay !== 'N/A');
  const hasEntries = Boolean(totalEntriesDisplay && totalEntriesDisplay !== 'N/A');
  if (!hasRank) return 'N/A';
  if (hasEntries) return `#${rankDisplay} / ${totalEntriesDisplay}`;
  return `#${rankDisplay}`;
};

const GAME_DIFF_SHORT: Record<number, string> = {
  [-1]: '',
  [0]: 'E',
  [1]: 'M',
  [2]: 'H',
  [3]: 'X',
};

const selectTracker = (ld: LeaderboardData, key: InstrumentKey): ScoreTracker | undefined => (ld as any)[key] as
  | ScoreTracker
  | undefined;

const fallbackDifficulty = (song: Song, key: InstrumentKey): number => getSongInstrumentDifficulty(song, key) ?? 0;

export const buildSongInfoInstrumentRows = (params: {
  song: Song;
  instrumentOrder: ReadonlyArray<InstrumentKey>;
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>;
}): SongInfoInstrumentRow[] => {
  const {song, instrumentOrder, scoresIndex} = params;
  const ld = scoresIndex[song.track.su];

  const rows: SongInfoInstrumentRow[] = [];

  for (const key of instrumentOrder) {
    if (!songSupportsInstrument(song, key)) continue;

    const tr = ld ? selectTracker(ld, key) : undefined;

    const hasScore = Boolean(tr?.initialized);
    const isFullCombo = Boolean(tr?.initialized && tr.isFullCombo);
    const starsCount = hasScore ? tr!.numStars : 0;
    const gameDifficultyDisplay = hasScore ? GAME_DIFF_SHORT[tr!.gameDifficulty] || undefined : undefined;
    const scoreDisplay = hasScore ? formatIntegerWithCommas(tr!.maxScore) : '0';
    const percentDisplay = hasScore ? (isFullCombo ? '100%' : formatPercent(tr!.percentHit)) : '0%';
    const seasonDisplay = hasScore ? formatSeason(tr!.seasonAchieved) : 'N/A';

    const percentileDisplay = hasScore ? tr!.leaderboardPercentileFormatted || 'N/A' : 'N/A';
    const rankDisplay = hasScore && tr!.rank > 0 ? formatIntegerWithCommas(tr!.rank) : 'N/A';
    const totalEntriesDisplay =
      hasScore && tr!.calculatedNumEntries > 0 ? formatIntegerWithCommas(tr!.calculatedNumEntries) : 'N/A';

    const isTop5Percentile = Boolean(hasScore && tr!.rawPercentile > 0 && tr!.rawPercentile <= 0.05);

    const rawDifficulty = hasScore ? tr!.difficulty : 0;
    const diff = rawDifficulty !== 0 ? rawDifficulty : fallbackDifficulty(song, key);

    rows.push({
      key,
      name: keyToDisplayName(key),
      icon: `${key}.png`,
      hasScore,
      isFullCombo,
      starsCount,
      gameDifficultyDisplay,
      scoreDisplay,
      percentDisplay,
      seasonDisplay,
      percentileDisplay,
      rankDisplay,
      totalEntriesDisplay,
      rankOutOfDisplay: composeRankOutOf(rankDisplay, totalEntriesDisplay),
      isTop5Percentile,
      rawDifficulty: diff,
    });
  }

  return rows;
};
