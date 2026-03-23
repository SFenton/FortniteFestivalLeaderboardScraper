/**
 * Barrel export for Songs first-run slides.
 * Composes all slide definitions into a single array based on platform.
 */
import { songListSlide } from './pages/SongList';
import { sortSlide } from './pages/Sort';
import { navigationMobileSlide, navigationDesktopSlide } from './pages/Navigation';
import { filterSlide } from './pages/Filter';
import { songIconsSlide } from './pages/SongIcons';
import { metadataSlide } from './pages/MetadataFilter';
import type { FirstRunSlideDef } from '../../../firstRun/types';

export function songSlides(isMobile: boolean): FirstRunSlideDef[] {
  const navSlide = isMobile ? navigationMobileSlide : navigationDesktopSlide;
  return [songListSlide, sortSlide, navSlide, filterSlide, songIconsSlide, metadataSlide];
}
