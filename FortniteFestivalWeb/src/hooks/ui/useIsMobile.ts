import { IS_IOS, IS_ANDROID, IS_PWA } from '@festival/ui-utils';
import { useMediaQuery } from './useMediaQuery';
import { MOBILE_BREAKPOINT } from '@festival/theme';

const QUERY = `(max-width: ${MOBILE_BREAKPOINT}px)`;

/** True when viewport is narrow (dimension-based only). Use for layout decisions (card style, grids). */
export function useIsMobile(): boolean {
  return useMediaQuery(QUERY);
}

/** True when mobile chrome (bottom nav, FAB, mobile header) should be shown — always on iOS/Android/PWA. */
export function useIsMobileChrome(): boolean {
  const dimensionMobile = useMediaQuery(QUERY);
  return dimensionMobile || IS_IOS || IS_ANDROID || IS_PWA;
}
