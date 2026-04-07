import { describe, it, expect, vi, beforeAll } from 'vitest';
import { render } from '@testing-library/react';
import { stubResizeObserver } from '../../../helpers/browserStubs';
import { TestProviders } from '../../../helpers/TestProviders';

// Mock slide height
vi.mock('../../../../src/firstRun/SlideHeightContext', () => ({
  SlideHeightContext: { Provider: ({ children }: any) => children },
  useSlideHeight: () => 400,
}));

vi.mock('../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobile: () => false,
  useIsMobileChrome: () => false,
  useIsWideDesktop: () => true,
  useIsNarrow: () => false,
}));

vi.mock('../../../../src/hooks/data/useDemoSongs', () => ({
  useDemoSongs: () => ({
    rows: [{ title: 'Demo', artist: 'Artist', year: 2024, albumArt: '' }],
    fadingIdx: new Set(),
    initialDone: true,
    pool: [],
  }),
  FADE_MS: 300,
  shuffle: <T,>(arr: readonly T[]): T[] => [...arr],
}));

import { statisticsSlides } from '../../../../src/pages/player/firstRun';
import { drillDownSlide } from '../../../../src/pages/player/firstRun/pages/DrillDown';
import { overviewSlide } from '../../../../src/pages/player/firstRun/pages/Overview';
import { instrumentBreakdownSlide } from '../../../../src/pages/player/firstRun/pages/InstrumentBreakdown';
import { percentilesSlide } from '../../../../src/pages/player/firstRun/pages/Percentiles';
import { topSongsSlide } from '../../../../src/pages/player/firstRun/pages/TopSongs';

beforeAll(() => {
  stubResizeObserver();
});

describe('statisticsSlides', () => {
  it('returns 5 slides', () => {
    expect(statisticsSlides).toHaveLength(5);
  });

  it('includes slides in the correct order', () => {
    expect(statisticsSlides[0]).toBe(drillDownSlide);
    expect(statisticsSlides[1]).toBe(overviewSlide);
    expect(statisticsSlides[2]).toBe(instrumentBreakdownSlide);
    expect(statisticsSlides[3]).toBe(percentilesSlide);
    expect(statisticsSlides[4]).toBe(topSongsSlide);
  });
});

describe('slide definitions', () => {
  const slides = [drillDownSlide, overviewSlide, instrumentBreakdownSlide, percentilesSlide, topSongsSlide];

  it.each(slides.map(s => [s.id, s]))('%s has required fields', (_id, slide) => {
    expect(slide.id).toBeTruthy();
    expect(slide.version).toBeGreaterThanOrEqual(1);
    expect(slide.title).toBeTruthy();
    expect(slide.description).toBeTruthy();
    expect(typeof slide.render).toBe('function');
  });

  it('drillDownSlide has correct id and version', () => {
    expect(drillDownSlide.id).toBe('statistics-drill-down');
    expect(drillDownSlide.version).toBe(1);
  });

  it('overviewSlide has correct id and version', () => {
    expect(overviewSlide.id).toBe('statistics-overview');
    expect(overviewSlide.version).toBe(2);
  });

  it('instrumentBreakdownSlide has correct id and version', () => {
    expect(instrumentBreakdownSlide.id).toBe('statistics-instrument-breakdown');
    expect(instrumentBreakdownSlide.version).toBe(1);
  });

  it('percentilesSlide has correct id and version', () => {
    expect(percentilesSlide.id).toBe('statistics-percentiles');
    expect(percentilesSlide.version).toBe(1);
  });

  it('topSongsSlide has correct id and version', () => {
    expect(topSongsSlide.id).toBe('statistics-top-songs');
    expect(topSongsSlide.version).toBe(1);
  });

  it('no slide has a gate property', () => {
    for (const slide of slides) {
      expect(slide.gate).toBeUndefined();
    }
  });

  it('each slide has a unique id', () => {
    const ids = slides.map(s => s.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('each slide has a contentStaggerCount', () => {
    for (const slide of slides) {
      expect(slide.contentStaggerCount).toBeGreaterThanOrEqual(1);
    }
  });

  it.each(slides.map(s => [s.id, s]))('%s render() produces a React element', (_id, slide) => {
    const { container } = render(slide.render(), { wrapper: TestProviders });
    expect(container.firstChild).toBeTruthy();
  });
});
