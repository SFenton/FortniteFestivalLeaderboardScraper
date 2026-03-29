import { describe, it, expect } from 'vitest';
import { songInfoSlides } from '../../../../src/pages/songinfo/firstRun';
import { chartSlide } from '../../../../src/pages/songinfo/firstRun/pages/Chart';
import { barSelectSlide } from '../../../../src/pages/songinfo/firstRun/pages/BarSelect';
import { viewAllSlide } from '../../../../src/pages/songinfo/firstRun/pages/ViewAll';
import { topScoresSlide } from '../../../../src/pages/songinfo/firstRun/pages/TopScores';
import { pathsMobileSlide, pathsDesktopSlide } from '../../../../src/pages/songinfo/firstRun/pages/PathPreview';
import { shopButtonMobileSlide, shopButtonDesktopSlide } from '../../../../src/pages/songinfo/firstRun/pages/ShopButton';
import { historySlide } from '../../../../src/pages/songinfo/firstRun/pages/History';

describe('songInfoSlides', () => {
  it('returns 7 slides', () => {
    expect(songInfoSlides(false)).toHaveLength(7);
    expect(songInfoSlides(true)).toHaveLength(7);
  });

  it('returns desktop paths slide when isMobile is false', () => {
    const slides = songInfoSlides(false);
    expect(slides[4]).toBe(pathsDesktopSlide);
  });

  it('returns mobile paths slide when isMobile is true', () => {
    const slides = songInfoSlides(true);
    expect(slides[4]).toBe(pathsMobileSlide);
  });

  it('returns desktop shop slide when isMobile is false', () => {
    const slides = songInfoSlides(false);
    expect(slides[5]).toBe(shopButtonDesktopSlide);
  });

  it('returns mobile shop slide when isMobile is true', () => {
    const slides = songInfoSlides(true);
    expect(slides[5]).toBe(shopButtonMobileSlide);
  });

  it('includes chart, barSelect, viewAll, topScores in both modes', () => {
    for (const isMobile of [true, false]) {
      const slides = songInfoSlides(isMobile);
      expect(slides[0]).toBe(chartSlide);
      expect(slides[1]).toBe(barSelectSlide);
      expect(slides[2]).toBe(viewAllSlide);
      expect(slides[3]).toBe(topScoresSlide);
    }
  });
});

describe('slide definitions', () => {
  const slides = [chartSlide, barSelectSlide, viewAllSlide, topScoresSlide, pathsMobileSlide, pathsDesktopSlide, shopButtonMobileSlide, shopButtonDesktopSlide, historySlide];

  it.each(slides.map(s => [s.id, s]))('%s has required fields', (_id, slide) => {
    expect(slide.id).toBeTruthy();
    expect(slide.version).toBeGreaterThanOrEqual(1);
    expect(slide.title).toBeTruthy();
    expect(slide.description).toBeTruthy();
    expect(typeof slide.render).toBe('function');
  });

  it('chartSlide gates on hasPlayer', () => {
    expect(chartSlide.gate!({ hasPlayer: true })).toBe(true);
    expect(chartSlide.gate!({ hasPlayer: false })).toBe(false);
  });

  it('barSelectSlide gates on hasPlayer', () => {
    expect(barSelectSlide.gate!({ hasPlayer: true })).toBe(true);
    expect(barSelectSlide.gate!({ hasPlayer: false })).toBe(false);
  });

  it('viewAllSlide gates on hasPlayer', () => {
    expect(viewAllSlide.gate!({ hasPlayer: true })).toBe(true);
    expect(viewAllSlide.gate!({ hasPlayer: false })).toBe(false);
  });

  it('historySlide gates on hasPlayer', () => {
    expect(historySlide.gate!({ hasPlayer: true })).toBe(true);
    expect(historySlide.gate!({ hasPlayer: false })).toBe(false);
  });

  it('topScoresSlide has no gate', () => {
    expect(topScoresSlide.gate).toBeUndefined();
  });

  it('paths slides have no gate', () => {
    expect(pathsMobileSlide.gate).toBeUndefined();
    expect(pathsDesktopSlide.gate).toBeUndefined();
  });

  it('paths slides share the same id but differ in description', () => {
    expect(pathsMobileSlide.id).toBe(pathsDesktopSlide.id);
    expect(pathsMobileSlide.description).not.toBe(pathsDesktopSlide.description);
  });

  it('shop slides have no gate', () => {
    expect(shopButtonMobileSlide.gate).toBeUndefined();
    expect(shopButtonDesktopSlide.gate).toBeUndefined();
  });

  it('shop slides share the same id but differ in description', () => {
    expect(shopButtonMobileSlide.id).toBe(shopButtonDesktopSlide.id);
    expect(shopButtonMobileSlide.description).not.toBe(shopButtonDesktopSlide.description);
  });

  it('slide render() returns JSX', () => {
    for (const slide of slides) {
      const el = slide.render();
      expect(el).toBeTruthy();
    }
  });

  it('all slides are at version 2 except history', () => {
    expect(chartSlide.version).toBe(2);
    expect(barSelectSlide.version).toBe(2);
    expect(viewAllSlide.version).toBe(2);
    expect(topScoresSlide.version).toBe(2);
    expect(pathsMobileSlide.version).toBe(2);
    expect(pathsDesktopSlide.version).toBe(2);
    expect(historySlide.version).toBe(1);
  });
});
