/**
 * Barrel export for Player History first-run slides.
 */
import { scoreListSlide } from './pages/ScoreList';
import { sortMobileSlide, sortDesktopSlide } from './pages/SortControls';
import type { FirstRunSlideDef } from '../../../../firstRun/types';

export function playerHistorySlides(isMobile: boolean): FirstRunSlideDef[] {
  return [
    scoreListSlide,
    isMobile ? sortMobileSlide : sortDesktopSlide,
  ];
}
