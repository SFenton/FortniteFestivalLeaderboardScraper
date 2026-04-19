import { MEDIUM_BREAKPOINT, MOBILE_BREAKPOINT } from '@festival/theme';

export const TOP_SCORES_COMPACT_BREAKPOINT = MEDIUM_BREAKPOINT;

export function resolveTopScoresColumns(cardWidth: number | undefined) {
  const width = cardWidth ?? 0;

  return {
    isCompactCard: width > 0 && width < TOP_SCORES_COMPACT_BREAKPOINT,
    showAccuracy: width >= TOP_SCORES_COMPACT_BREAKPOINT,
    showSeason: width >= TOP_SCORES_COMPACT_BREAKPOINT,
    showStars: width >= MOBILE_BREAKPOINT,
  };
}