/**
 * Re-export everything from @festival/theme.
 *
 * Local web code should import from '@festival/theme' directly; this barrel
 * exists only so that existing '../theme' imports continue to resolve while
 * we migrate them.
 */
export {
  Colors,
  Radius, Font, LineHeight, Gap, Opacity, Size, MaxWidth, Layout,
  goldFill, goldOutline, goldOutlineSkew,
  frostedCard, frostedCardLight,
  MOBILE_BREAKPOINT, NARROW_BREAKPOINT, MEDIUM_BREAKPOINT,
  STAGGER_INTERVAL, FADE_DURATION, SPINNER_FADE_MS, DEBOUNCE_MS, RESIZE_DEBOUNCE_MS, SETTINGS_RESTAGGER_DELAY,
  LEADERBOARD_PAGE_SIZE, SUGGESTIONS_BATCH_SIZE, SUGGESTIONS_INITIAL_BATCH,
  SYNC_POLL_ACTIVE_MS, SYNC_POLL_IDLE_MS,
} from '@festival/theme';
export type { ColorKey } from '@festival/theme';
