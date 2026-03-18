/**
 * Pure filter-building helpers extracted from PlayerContent.
 * Used by stat card onClick handlers to navigate to the songs page
 * with specific filter presets.
 */
import type { SongSettings, SongFilters } from '../../../utils/songSettings';
import type { ServerInstrumentKey as InstrumentKey } from '@festival/core/api/serverTypes';

export const PERCENTILE_THRESHOLDS = [1, 2, 3, 4, 5, 10, 15, 20, 25, 30, 40, 50, 60, 70, 80, 90, 100] as const;

/**
 * Build "clean" filters that reset the current instrument's missing/has toggles
 * and clear instrument-specific filters (season, percentile, stars, difficulty).
 * Preserves other instruments' states.
 */
export function cleanFilters(s: SongSettings, inst: InstrumentKey): SongFilters {
  return {
    ...s.filters,
    seasonFilter: {},
    percentileFilter: {},
    starsFilter: {},
    difficultyFilter: {},
    missingScores: { ...s.filters.missingScores, [inst]: false },
    missingFCs: { ...s.filters.missingFCs, [inst]: false },
    hasScores: { ...s.filters.hasScores, [inst]: false },
    hasFCs: { ...s.filters.hasFCs, [inst]: false },
  };
}

/**
 * Build a star-filter preset where only the given star key is enabled.
 */
export function buildStarFilter(starKey: number): Record<number, boolean> {
  return { 0: false, 1: false, 2: false, 3: false, 4: false, 5: false, 6: false, [starKey]: true };
}

/**
 * Build a percentile-filter preset where only the given bucket is enabled.
 */
export function buildPercentileFilter(pct: number): Record<number, boolean> {
  const filter: Record<number, boolean> = {};
  for (const t of PERCENTILE_THRESHOLDS) filter[t] = t === pct;
  filter[0] = false;
  return filter;
}

/** Returns a gold color string if percentile display matches Top 1–5%. */
export function percentileGoldColor(v: string): string | undefined {
  return /^Top [1-5]%$/.test(v) ? undefined : undefined;
}
