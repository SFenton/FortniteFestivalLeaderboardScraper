import { useMediaQuery } from './useMediaQuery';
import { ACCURACY_BREAKPOINT, SEASON_BREAKPOINT, MOBILE_BREAKPOINT } from '@festival/theme';

const ACCURACY_QUERY = `(min-width: ${ACCURACY_BREAKPOINT}px)`;
const SEASON_QUERY = `(min-width: ${SEASON_BREAKPOINT}px)`;
const STARS_QUERY = `(min-width: ${MOBILE_BREAKPOINT}px)`;

/** Responsive column visibility for leaderboards and history pages. */
export function useLeaderboardColumns() {
  const showAccuracy = useMediaQuery(ACCURACY_QUERY);
  const showSeason = useMediaQuery(SEASON_QUERY);
  const showStars = useMediaQuery(STARS_QUERY);
  return { showAccuracy, showSeason, showStars };
}
