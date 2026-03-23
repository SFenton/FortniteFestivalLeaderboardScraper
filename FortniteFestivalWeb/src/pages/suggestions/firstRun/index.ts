/**
 * Barrel export for Suggestions first-run slides.
 */
import { categoryCardSlide } from './pages/CategoryCardSlide';
import { globalFilterSlide } from './pages/GlobalFilter';
import { instrumentFilterSlide } from './pages/InstrumentFilter';
import { infiniteScrollSlide } from './pages/InfiniteScroll';
import type { FirstRunSlideDef } from '../../../firstRun/types';

export const suggestionsSlides: FirstRunSlideDef[] = [
  categoryCardSlide,
  globalFilterSlide,
  instrumentFilterSlide,
  infiniteScrollSlide,
];
