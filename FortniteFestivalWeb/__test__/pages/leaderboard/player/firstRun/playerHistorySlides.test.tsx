import { describe, it, expect } from 'vitest';
import { playerHistorySlides } from '../../../../../src/pages/leaderboard/player/firstRun';
import { scoreListSlide } from '../../../../../src/pages/leaderboard/player/firstRun/pages/ScoreList';
import { sortMobileSlide, sortDesktopSlide } from '../../../../../src/pages/leaderboard/player/firstRun/pages/SortControls';

describe('playerHistorySlides', () => {
  it('returns 2 slides', () => {
    expect(playerHistorySlides(false)).toHaveLength(2);
    expect(playerHistorySlides(true)).toHaveLength(2);
  });

  it('returns desktop sort slide when isMobile is false', () => {
    const slides = playerHistorySlides(false);
    expect(slides[1]).toBe(sortDesktopSlide);
  });

  it('returns mobile sort slide when isMobile is true', () => {
    const slides = playerHistorySlides(true);
    expect(slides[1]).toBe(sortMobileSlide);
  });

  it('includes scoreListSlide in both modes', () => {
    expect(playerHistorySlides(true)[0]).toBe(scoreListSlide);
    expect(playerHistorySlides(false)[0]).toBe(scoreListSlide);
  });
});

describe('slide definitions', () => {
  const slides = [scoreListSlide, sortMobileSlide, sortDesktopSlide];

  it.each(slides.map(s => [s.id, s]))('%s has required fields', (_id, slide) => {
    expect(slide.id).toBeTruthy();
    expect(slide.version).toBeGreaterThanOrEqual(1);
    expect(slide.title).toBeTruthy();
    expect(slide.description).toBeTruthy();
    expect(typeof slide.render).toBe('function');
  });

  it('no slide has a gate', () => {
    for (const slide of slides) {
      expect(slide.gate).toBeUndefined();
    }
  });

  it('sort slides share the same id but differ in description', () => {
    expect(sortMobileSlide.id).toBe(sortDesktopSlide.id);
    expect(sortMobileSlide.description).not.toBe(sortDesktopSlide.description);
  });

  it('sort slides share a contentKey so viewport variants reuse seen-state', () => {
    expect(sortMobileSlide.contentKey).toBe('playerhistory-sort');
    expect(sortDesktopSlide.contentKey).toBe('playerhistory-sort');
  });

  it('slide render() returns JSX', () => {
    for (const slide of slides) {
      expect(slide.render()).toBeTruthy();
    }
  });

  it('all slides are at version 1', () => {
    for (const slide of slides) {
      expect(slide.version).toBe(1);
    }
  });
});
