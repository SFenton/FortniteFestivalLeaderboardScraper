/** Responsive layout breakpoints (px). */
export const MOBILE_BREAKPOINT = 768;
export const NARROW_BREAKPOINT = 420;
export const MEDIUM_BREAKPOINT = 520;

/** Semantic aliases for feature-specific breakpoints. */
export const ACCURACY_BREAKPOINT = NARROW_BREAKPOINT;
export const SEASON_BREAKPOINT = MEDIUM_BREAKPOINT;

export const WIDE_DESKTOP_BREAKPOINT = 1440;

/** Pre-built media query strings. */
export const QUERY_SHOW_ACCURACY = `(min-width: ${NARROW_BREAKPOINT}px)`;
export const QUERY_SHOW_SEASON = `(min-width: ${MEDIUM_BREAKPOINT}px)`;
export const QUERY_SHOW_STARS = `(min-width: ${MOBILE_BREAKPOINT}px)`;
export const QUERY_MOBILE = `(max-width: ${MOBILE_BREAKPOINT}px)`;
export const QUERY_NARROW_GRID = `(max-width: ${NARROW_BREAKPOINT - 1}px)`;
export const QUERY_WIDE_DESKTOP = `(min-width: ${WIDE_DESKTOP_BREAKPOINT}px)`;
