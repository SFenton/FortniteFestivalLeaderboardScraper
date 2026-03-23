import { createContext, useContext } from 'react';

/**
 * Provides the available content height (px) inside the FirstRunCarousel
 * slide area.  Updated by a ResizeObserver in FirstRunCarousel so child
 * demo components can adapt their layout without reading a CSS custom
 * property (which can return stale values inside RO callbacks).
 */
export const SlideHeightContext = createContext(0);

/** Read the current slide-content height from the nearest FirstRunCarousel. */
export function useSlideHeight(): number {
  return useContext(SlideHeightContext);
}
