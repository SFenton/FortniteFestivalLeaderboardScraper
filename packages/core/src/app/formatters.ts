import type {InstrumentKey} from '../instruments';
import { LEADERBOARD_PERCENTILE_THRESHOLDS } from '../songListConfig';

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

const PERCENTILE_BUCKETS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;

/** Clamp a raw percentile (0–100) to the nearest bucket and return e.g. "Top 5%". */
export const formatPercentileBucket = (pct: number): string => {
  const clamped = Math.max(1, Math.min(100, pct));
  const bucket = PERCENTILE_BUCKETS.find(t => clamped <= t) ?? 100;
  return `Top ${bucket}%`;
};

/**
 * Format an account-level ranking percentile from rank and total accounts.
 * Uses granular sub-1% buckets (0.01–0.5) for leaderboard display.
 * Returns undefined when totalAccounts is 0.
 */
export const formatLeaderboardPercentile = (rank: number, totalAccounts: number): string | undefined => {
  if (totalAccounts <= 0) return undefined;
  const pct = (rank / totalAccounts) * 100;
  const clamped = Math.max(0.01, Math.min(100, pct));
  const bucket = LEADERBOARD_PERCENTILE_THRESHOLDS.find(t => clamped <= t) ?? 100;
  return `Top ${bucket}%`;
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

/**
 * Interpolate an accuracy percentage (0-100) to an RGB color string.
 * 0% → red (220,40,40), 100% → green (46,204,113).
 */
export function accuracyColor(pct: number): string {
  const t = clamp(pct / 100, 0, 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgb(${r},${g},${b})`;
}

/**
 * Interpolate an accuracy percentage (0-100) to a semi-transparent RGBA color
 * string suitable for pill backgrounds on dark themes.
 * Uses the same red→green scale as {@link accuracyColor} at 25% opacity.
 */
export function accuracyBgColor(pct: number): string {
  const t = clamp(pct / 100, 0, 1);
  const r = Math.round(220 * (1 - t) + 46 * t);
  const g = Math.round(40 * (1 - t) + 204 * t);
  const b = Math.round(40 * (1 - t) + 113 * t);
  return `rgba(${r},${g},${b},0.25)`;
}

/**
 * Interpolate a rank position to an RGB color string on the red→green scale.
 * Rank 1 out of many → near-green; last rank → near-red.
 * Returns a neutral gray when totalAccounts or rank is invalid.
 */
export function rankColor(rank: number, totalAccounts: number): string {
  if (totalAccounts <= 0 || rank <= 0) return 'rgb(127,140,141)';
  const pct = (1 - rank / totalAccounts) * 100;
  return accuracyColor(pct);
}

/**
 * Interpolate a max-score percentage (0-100) to an RGB color string.
 * 0% → red (220,40,40), 100% → darker green (34,139,34).
 */
export function maxScoreColor(pct: number): string {
  const t = clamp(pct / 100, 0, 1);
  const r = Math.round(220 * (1 - t) + 34 * t);
  const g = Math.round(40 * (1 - t) + 139 * t);
  const b = Math.round(40 * (1 - t) + 34 * t);
  return `rgb(${r},${g},${b})`;
}

/**
 * Calculate the CSS `ch` width needed to display the largest score in a list.
 */
export function calculateScoreWidth(scores: { score: number }[]): string {
  if (scores.length === 0) return '1ch';
  const maxLen = Math.max(...scores.map((s) => s.score.toLocaleString().length), 1);
  return `${maxLen}ch`;
}
