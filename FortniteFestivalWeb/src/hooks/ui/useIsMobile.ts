import { IS_IOS, IS_ANDROID, IS_PWA } from '@festival/ui-utils';
import { useMediaQuery } from './useMediaQuery';
import { MOBILE_BREAKPOINT, WIDE_DESKTOP_BREAKPOINT, QUERY_NARROW_GRID } from '@festival/theme';

const QUERY = `(max-width: ${MOBILE_BREAKPOINT}px)`;
const WIDE_QUERY = `(min-width: ${WIDE_DESKTOP_BREAKPOINT}px)`;

/** True when viewport is narrow (dimension-based only). Use for layout decisions (card style, grids). */
export function useIsMobile(): boolean {
  return useMediaQuery(QUERY);
}

/** True when mobile chrome (bottom nav, FAB, mobile header) should be shown — always on iOS/Android/PWA. */
export function useIsMobileChrome(): boolean {
  const dimensionMobile = useMediaQuery(QUERY);
  return dimensionMobile || IS_IOS || IS_ANDROID || IS_PWA;
}

/** True when viewport is wide enough for a persistent pinned sidebar (≥1200px). */
export function useIsWideDesktop(): boolean {
  return useMediaQuery(WIDE_QUERY);
}

/** True when viewport is very narrow (<420px). Use for two-row card layouts. */
export function useIsNarrow(): boolean {
  return useMediaQuery(QUERY_NARROW_GRID);
}
