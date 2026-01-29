import type {InstrumentKey} from '../../core/instruments';
import type {LeaderboardData, ScoreTracker, Song} from '../../core/models';

export type SongInfoInstrumentRow = {
  key: InstrumentKey;
  name: string;
  icon: string;
  hasScore: boolean;
  isFullCombo: boolean;
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
      return 'Vocals';
    case 'bass':
      return 'Bass';
    case 'pro_guitar':
      return 'Pro Guitar';
    case 'pro_bass':
      return 'Pro Bass';
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
    default:
      return 0;
  }
};

export const buildSongInfoInstrumentRows = (params: {
  song: Song;
  instrumentOrder: ReadonlyArray<InstrumentKey>;
  scoresIndex: Readonly<Record<string, LeaderboardData | undefined>>;
}): SongInfoInstrumentRow[] => {
  const {song, instrumentOrder, scoresIndex} = params;
  const ld = scoresIndex[song.track.su];

  const rows: SongInfoInstrumentRow[] = [];

  for (const key of instrumentOrder) {
    const tr = ld ? selectTracker(ld, key) : undefined;

    const hasScore = Boolean(tr?.initialized);
    const isFullCombo = Boolean(tr?.initialized && tr.isFullCombo);
    const scoreDisplay = hasScore ? String(tr!.maxScore) : '0';
    const percentDisplay = hasScore ? (isFullCombo ? '100%' : formatPercent(tr!.percentHit)) : '0%';
    const seasonDisplay = hasScore ? formatSeason(tr!.seasonAchieved) : 'N/A';

    const percentileDisplay = hasScore ? tr!.leaderboardPercentileFormatted || 'N/A' : 'N/A';
    const rankDisplay = hasScore && tr!.rank > 0 ? String(tr!.rank) : 'N/A';
    const totalEntriesDisplay = hasScore && tr!.calculatedNumEntries > 0 ? String(tr!.calculatedNumEntries) : 'N/A';

    const isTop5Percentile = Boolean(hasScore && tr!.rawPercentile > 0 && tr!.rawPercentile <= 0.05);

    const rawDifficulty = hasScore ? tr!.difficulty : 0;
    const diff = rawDifficulty !== 0 ? rawDifficulty : fallbackDifficulty(song, key);

    rows.push({
      key,
      name: keyToDisplayName(key),
      icon: `${key}.png`,
      hasScore,
      isFullCombo,
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
