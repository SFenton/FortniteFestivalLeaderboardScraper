import { describe, it, expect } from 'vitest';
import { songSlides } from '../../../../src/pages/songs/firstRun';
import { songListSlide } from '../../../../src/pages/songs/firstRun/pages/SongList';
import { sortSlide } from '../../../../src/pages/songs/firstRun/pages/Sort';
import { navigationMobileSlide, navigationDesktopSlide } from '../../../../src/pages/songs/firstRun/pages/Navigation';
import { filterSlide } from '../../../../src/pages/songs/firstRun/pages/Filter';
import { songIconsSlide } from '../../../../src/pages/songs/firstRun/pages/SongIcons';
import { metadataSlide } from '../../../../src/pages/songs/firstRun/pages/MetadataFilter';

describe('songSlides', () => {
  it('returns 6 slides', () => {
    expect(songSlides(false)).toHaveLength(6);
    expect(songSlides(true)).toHaveLength(6);
  });

  it('returns desktop navigation slide when isMobile is false', () => {
    const slides = songSlides(false);
    expect(slides[2]).toBe(navigationDesktopSlide);
  });

  it('returns mobile navigation slide when isMobile is true', () => {
    const slides = songSlides(true);
    expect(slides[2]).toBe(navigationMobileSlide);
  });

  it('includes songList, sort, filter, songIcons, metadata in both modes', () => {
    for (const isMobile of [true, false]) {
      const slides = songSlides(isMobile);
      expect(slides[0]).toBe(songListSlide);
      expect(slides[1]).toBe(sortSlide);
      expect(slides[3]).toBe(filterSlide);
      expect(slides[4]).toBe(songIconsSlide);
      expect(slides[5]).toBe(metadataSlide);
    }
  });
});

describe('slide definitions', () => {
  const slides = [songListSlide, sortSlide, navigationMobileSlide, navigationDesktopSlide, filterSlide, songIconsSlide, metadataSlide];

  it.each(slides.map(s => [s.id, s]))('%s has required fields', (_id, slide) => {
    expect(slide.id).toBeTruthy();
    expect(slide.version).toBeGreaterThanOrEqual(1);
    expect(slide.title).toBeTruthy();
    expect(slide.description).toBeTruthy();
    expect(typeof slide.render).toBe('function');
  });

  it('filterSlide gates on hasPlayer', () => {
    expect(filterSlide.gate!({ hasPlayer: true })).toBe(true);
    expect(filterSlide.gate!({ hasPlayer: false })).toBe(false);
  });

  it('songIconsSlide gates on hasPlayer', () => {
    expect(songIconsSlide.gate!({ hasPlayer: true })).toBe(true);
    expect(songIconsSlide.gate!({ hasPlayer: false })).toBe(false);
  });

  it('metadataSlide gates on hasPlayer', () => {
    expect(metadataSlide.gate!({ hasPlayer: true })).toBe(true);
    expect(metadataSlide.gate!({ hasPlayer: false })).toBe(false);
  });

  it('songListSlide has no gate', () => {
    expect(songListSlide.gate).toBeUndefined();
  });

  it('sortSlide has no gate', () => {
    expect(sortSlide.gate).toBeUndefined();
  });

  it('navigation slides have no gate', () => {
    expect(navigationMobileSlide.gate).toBeUndefined();
    expect(navigationDesktopSlide.gate).toBeUndefined();
  });

  it('navigation slides share the same id but differ in description', () => {
    expect(navigationMobileSlide.id).toBe(navigationDesktopSlide.id);
    expect(navigationMobileSlide.description).not.toBe(navigationDesktopSlide.description);
  });

  it('slide render() returns JSX', () => {
    for (const slide of slides) {
      const el = slide.render();
      expect(el).toBeTruthy();
    }
  });

  it('slide versions are correct', () => {
    expect(songListSlide.version).toBe(3);
    expect(sortSlide.version).toBe(5);
    expect(navigationMobileSlide.version).toBe(5);
    expect(navigationDesktopSlide.version).toBe(5);
    expect(filterSlide.version).toBe(4);
    expect(songIconsSlide.version).toBe(3);
    expect(metadataSlide.version).toBe(3);
  });

  it('slide ids are correct', () => {
    expect(songListSlide.id).toBe('songs-song-list');
    expect(sortSlide.id).toBe('songs-sort');
    expect(navigationMobileSlide.id).toBe('songs-navigation');
    expect(navigationDesktopSlide.id).toBe('songs-navigation');
    expect(filterSlide.id).toBe('songs-filter');
    expect(songIconsSlide.id).toBe('songs-icons');
    expect(metadataSlide.id).toBe('songs-metadata');
  });
});
