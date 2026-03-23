/**
 * Barrel export for Song Info first-run slides.
 */
import { chartSlide } from './pages/Chart';
import { barSelectSlide } from './pages/BarSelect';
import { viewAllSlide } from './pages/ViewAll';
import { topScoresSlide } from './pages/TopScores';
import { pathsMobileSlide, pathsDesktopSlide } from './pages/PathPreview';
import type { FirstRunSlideDef } from '../../../firstRun/types';

export function songInfoSlides(isMobile: boolean): FirstRunSlideDef[] {
  return [
    chartSlide,
    barSelectSlide,
    viewAllSlide,
    topScoresSlide,
    isMobile ? pathsMobileSlide : pathsDesktopSlide,
  ];
}
