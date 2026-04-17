import type {InstrumentKey} from '../instruments';
import type {LeaderboardData, ScoreTracker} from '../models';

export type ScoreInstrument =
  | 'Lead'
  | 'Drums'
  | 'Vocals'
  | 'Bass'
  | 'ProLead'
  | 'ProBass'
  | 'PeripheralVocals'
  | 'PeripheralCymbals'
  | 'PeripheralDrums';
export type ScoreSortColumn = 'Title' | 'Artist' | 'Score' | 'Percent' | 'Stars' | 'FC' | 'Season';

export type ScoreRow = {
  songId: string;
  title: string;
  artist: string;
  score: number;
  percent: string;
  starText: string;
  maxStars: boolean;
  fullComboSymbol: string;
  isFullCombo: boolean;
  season: string;
};

const toFixedPct = (percentHit: number): string => `${(percentHit / 10000).toFixed(2)}%`;

const toStars = (n: number): string => {
  if (n <= 0) return '';
  // Keep it UI-agnostic; use '*' to match ScoreTracker.starsFormatted style.
  return '*'.repeat(Math.min(n, 6));
};

const instrumentToKey = (instrument: ScoreInstrument): InstrumentKey => {
  switch (instrument) {
    case 'Drums':
      return 'drums';
    case 'Vocals':
      return 'vocals';
    case 'Bass':
      return 'bass';
    case 'ProLead':
      return 'pro_guitar';
    case 'ProBass':
      return 'pro_bass';
    case 'PeripheralVocals':
      return 'peripheral_vocals';
    case 'PeripheralCymbals':
      return 'peripheral_cymbals';
    case 'PeripheralDrums':
      return 'peripheral_drums';
    case 'Lead':
    default:
      return 'guitar';
  }
};

const selectTracker = (ld: LeaderboardData, key: InstrumentKey): ScoreTracker | undefined => (ld as any)[key] as
  | ScoreTracker
  | undefined;

export const buildScoreRows = (params: {
  scores: ReadonlyArray<LeaderboardData>;
  instrument: ScoreInstrument;
  filterText?: string;
  sortColumn?: ScoreSortColumn;
  sortDesc?: boolean;
}): ScoreRow[] => {
  const key = instrumentToKey(params.instrument);
  const low = (params.filterText ?? '').trim().toLowerCase();
  const sortColumn = params.sortColumn ?? 'Title';
  const sortDesc = params.sortDesc ?? false;

  let list = [...params.scores];

  if (low) {
    list = list.filter(ld => {
      const title = (ld.title ?? '').toLowerCase();
      const artist = (ld.artist ?? '').toLowerCase();
      return title.includes(low) || artist.includes(low);
    });
  }

  const withTracker = list
    .map(ld => ({ld, t: selectTracker(ld, key)}))
    .filter(x => x.t && x.t.initialized) as Array<{ld: LeaderboardData; t: ScoreTracker}>;

  const sorted = (() => {
    switch (sortColumn) {
      case 'Artist':
        return withTracker.sort((a, b) => {
          const aa = a.ld.artist ?? '';
          const ba = b.ld.artist ?? '';
          if (aa !== ba) return aa.localeCompare(ba);
          return (a.ld.title ?? '').localeCompare(b.ld.title ?? '');
        });
      case 'Score':
        return withTracker.sort((a, b) => a.t.maxScore - b.t.maxScore);
      case 'Percent':
        return withTracker.sort((a, b) => a.t.percentHit - b.t.percentHit);
      case 'Stars':
        return withTracker.sort((a, b) => a.t.numStars - b.t.numStars);
      case 'FC':
        return withTracker.sort((a, b) => Number(a.t.isFullCombo) - Number(b.t.isFullCombo));
      case 'Season':
        return withTracker.sort((a, b) => a.t.seasonAchieved - b.t.seasonAchieved);
      case 'Title':
      default:
        return withTracker.sort((a, b) => (a.ld.title ?? '').localeCompare(b.ld.title ?? ''));
    }
  })();

  if (sortDesc) sorted.reverse();

  const rows: ScoreRow[] = [];
  for (const {ld, t} of sorted) {
    rows.push({
      songId: ld.songId ?? '',
      title: ld.title ?? '',
      artist: ld.artist ?? '',
      score: t.maxScore,
      percent: toFixedPct(t.percentHit),
      starText: toStars(t.numStars),
      maxStars: t.numStars >= 6,
      fullComboSymbol: t.isFullCombo ? 'FC' : '',
      isFullCombo: t.isFullCombo,
      season: t.seasonAchieved > 0 ? String(t.seasonAchieved) : 'All-Time',
    });
  }

  return rows;
};
