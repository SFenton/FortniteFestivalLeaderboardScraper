import type {InstrumentKey} from '../../core/instruments';

const clamp = (v: number, min: number, max: number): number => Math.max(min, Math.min(max, v));

const bankersRoundInt = (value: number): number => {
  if (!Number.isFinite(value)) return Number.NaN;

  const sign = value < 0 ? -1 : 1;
  const abs = Math.abs(value);
  const flo = Math.floor(abs);
  const frac = abs - flo;
  const eps = 1e-12;

  if (frac > 0.5 + eps) return sign * (flo + 1);
  if (frac < 0.5 - eps) return sign * flo;

  // Tie: round to even.
  return sign * (flo % 2 === 0 ? flo : flo + 1);
};

const bankersRound = (value: number, decimals: number): number => {
  if (!Number.isFinite(value)) return Number.NaN;
  if (!Number.isFinite(decimals) || decimals < 0) return Number.NaN;

  const factor = 10 ** decimals;
  const scaled = value * factor;
  const roundedScaled = bankersRoundInt(scaled);
  return roundedScaled / factor;
};

export const formatIntegerWithCommas = (value: number): string => {
  const i = Math.round(value);
  const s = String(Math.abs(i));
  const withCommas = s.replace(/\B(?=(\d{3})+(?!\d))/g, ',');
  return i < 0 ? `-${withCommas}` : withCommas;
};

/**
 * Port of MAUI `FormatScore(double score)` from `HomePage.xaml.cs` / `StatisticsPage.xaml.cs`.
 */
export const formatScoreCompact = (score: number): string => {
  if (!Number.isFinite(score)) return 'N/A';

  const abs = Math.abs(score);
  const sign = score < 0 ? '-' : '';

  if (abs >= 1_000_000_000) return `${sign}${(abs / 1_000_000_000).toFixed(2)}B`;
  if (abs >= 1_000_000) return `${sign}${(abs / 1_000_000).toFixed(2)}M`;
  if (abs >= 1_000) return `${sign}${(abs / 1_000).toFixed(1)}K`;

  return formatIntegerWithCommas(score);
};

/**
 * Port of MAUI `FormatPercentile(double rawPercentile)` from `HomePage.xaml.cs` / `StatisticsPage.xaml.cs`.
 *
 * `rawPercentile` is on 0..1 scale (smaller is better), e.g. 0.0144 => 1.44%.
 */
export const formatPercentileTopExact = (rawPercentile: number): string => {
  if (!Number.isFinite(rawPercentile)) return 'N/A';

  const topPct = clamp(rawPercentile * 100, 0.01, 100);

  if (topPct < 1) {
    const rounded = bankersRound(topPct, 2);
    if (!Number.isFinite(rounded)) return 'N/A';
    return `Top ${rounded.toFixed(2)}%`;
  }

  const rounded = bankersRound(topPct, 0);
  if (!Number.isFinite(rounded)) return 'N/A';
  return `Top ${rounded.toFixed(0)}%`;
};

export const instrumentKeyToLabel = (key: InstrumentKey): string => {
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

export const instrumentKeyToColorHex = (key: InstrumentKey): string => {
  switch (key) {
    case 'guitar':
      return '#b35cd6';
    case 'bass':
      return '#3498db';
    case 'drums':
      return '#e74c3c';
    case 'vocals':
      return '#27ae60';
    case 'pro_guitar':
      return '#9b59b6';
    case 'pro_bass':
      return '#2980b9';
    default:
      return '#7f8c8d';
  }
};
