import { describe, it, expect } from 'vitest';
import { suggestionsSlides } from '../../../../src/pages/suggestions/firstRun';
import { categoryCardSlide } from '../../../../src/pages/suggestions/firstRun/pages/CategoryCardSlide';
import { globalFilterSlide } from '../../../../src/pages/suggestions/firstRun/pages/GlobalFilter';
import { instrumentFilterSlide } from '../../../../src/pages/suggestions/firstRun/pages/InstrumentFilter';
import { infiniteScrollSlide } from '../../../../src/pages/suggestions/firstRun/pages/InfiniteScroll';

describe('suggestionsSlides', () => {
  it('returns 4 slides', () => {
    expect(suggestionsSlides).toHaveLength(4);
  });

  it('has slides in correct order', () => {
    expect(suggestionsSlides[0]).toBe(categoryCardSlide);
    expect(suggestionsSlides[1]).toBe(globalFilterSlide);
    expect(suggestionsSlides[2]).toBe(instrumentFilterSlide);
    expect(suggestionsSlides[3]).toBe(infiniteScrollSlide);
  });
});

describe('slide definitions', () => {
  const slides = [categoryCardSlide, globalFilterSlide, instrumentFilterSlide, infiniteScrollSlide];

  it.each(slides.map(s => [s.id, s]))('%s has required fields', (_id, slide) => {
    expect(slide.id).toBeTruthy();
    expect(slide.version).toBeGreaterThanOrEqual(1);
    expect(slide.title).toBeTruthy();
    expect(slide.description).toBeTruthy();
    expect(typeof slide.render).toBe('function');
  });

  it('slide ids are correct', () => {
    expect(categoryCardSlide.id).toBe('suggestions-category-card');
    expect(globalFilterSlide.id).toBe('suggestions-global-filter');
    expect(instrumentFilterSlide.id).toBe('suggestions-instrument-filter');
    expect(infiniteScrollSlide.id).toBe('suggestions-infinite-scroll');
  });

  it('slide versions are all 1', () => {
    for (const slide of slides) {
      expect(slide.version).toBe(1);
    }
  });

  it('no slide has a gate', () => {
    for (const slide of slides) {
      expect(slide.gate).toBeUndefined();
    }
  });

  it('slides have contentStaggerCount set', () => {
    expect(categoryCardSlide.contentStaggerCount).toBe(2);
    expect(globalFilterSlide.contentStaggerCount).toBe(3);
    expect(instrumentFilterSlide.contentStaggerCount).toBe(3);
    expect(infiniteScrollSlide.contentStaggerCount).toBe(1);
  });

  it('render() returns JSX', () => {
    for (const slide of slides) {
      const el = slide.render();
      expect(el).toBeTruthy();
    }
  });
});
