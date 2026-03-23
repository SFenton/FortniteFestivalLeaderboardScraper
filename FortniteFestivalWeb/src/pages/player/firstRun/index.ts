/**
 * Barrel export for Statistics first-run slides.
 */
import { overviewSlide } from './pages/Overview';
import { instrumentBreakdownSlide } from './pages/InstrumentBreakdown';
import { percentilesSlide } from './pages/Percentiles';
import { topSongsSlide } from './pages/TopSongs';
import { drillDownSlide } from './pages/DrillDown';
import type { FirstRunSlideDef } from '../../../firstRun/types';

export const statisticsSlides: FirstRunSlideDef[] = [
  drillDownSlide,
  overviewSlide,
  instrumentBreakdownSlide,
  percentilesSlide,
  topSongsSlide,
];
