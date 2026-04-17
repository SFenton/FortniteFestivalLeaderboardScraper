/**
 * Barrel export for Statistics first-run slides.
 * Composes all slide definitions into a single array based on platform.
 */
import { overviewSlide } from './pages/Overview';
import { instrumentBreakdownSlide } from './pages/InstrumentBreakdown';
import { percentilesSlide } from './pages/Percentiles';
import { topSongsSlide } from './pages/TopSongs';
import { drillDownSlide } from './pages/DrillDown';
import { selectProfileMobileSlide, selectProfileDesktopSlide } from './pages/SelectProfile';
import type { FirstRunSlideDef } from '../../../firstRun/types';

export function statisticsSlides(isMobile: boolean): FirstRunSlideDef[] {
  const selectProfileSlide = isMobile ? selectProfileMobileSlide : selectProfileDesktopSlide;
  return [
    selectProfileSlide,
    drillDownSlide,
    overviewSlide,
    instrumentBreakdownSlide,
    percentilesSlide,
    topSongsSlide,
  ];
}
