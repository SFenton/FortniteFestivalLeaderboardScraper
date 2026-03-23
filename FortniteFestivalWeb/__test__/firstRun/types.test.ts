import { describe, it, expect, beforeEach } from 'vitest';
import {
  loadSeenSlides,
  saveSeenSlides,
  contentHash,
  isSlideUnseen,
  type FirstRunSlideDef,
  type FirstRunStorage,
} from '../../src/firstRun/types';

const STORAGE_KEY = 'fst:firstRun';

function makeSlide(overrides: Partial<FirstRunSlideDef> = {}): FirstRunSlideDef {
  return {
    id: 'test-slide',
    version: 1,
    title: 'Test Title',
    description: 'Test Description',
    render: () => null,
    ...overrides,
  };
}

describe('contentHash', () => {
  it('returns consistent hash for the same input', () => {
    const h1 = contentHash('hello');
    const h2 = contentHash('hello');
    expect(h1).toBe(h2);
  });

  it('returns different hashes for different inputs', () => {
    expect(contentHash('abc')).not.toBe(contentHash('xyz'));
  });

  it('handles empty string', () => {
    const hash = contentHash('');
    expect(hash).toBe((5381 >>> 0).toString(16));
  });

  it('returns a hex string', () => {
    const hash = contentHash('test string');
    expect(hash).toMatch(/^[0-9a-f]+$/);
  });
});

describe('loadSeenSlides', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('returns empty object when localStorage has no entry', () => {
    expect(loadSeenSlides()).toEqual({});
  });

  it('returns empty object for invalid JSON', () => {
    localStorage.setItem(STORAGE_KEY, 'not-json{{{');
    expect(loadSeenSlides()).toEqual({});
  });

  it('returns parsed data for valid JSON', () => {
    const data: FirstRunStorage = {
      'slide-a': { version: 1, hash: 'abc', seenAt: '2024-01-01' },
    };
    localStorage.setItem(STORAGE_KEY, JSON.stringify(data));
    expect(loadSeenSlides()).toEqual(data);
  });
});

describe('saveSeenSlides', () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it('writes to localStorage', () => {
    const data: FirstRunStorage = {
      'slide-b': { version: 2, hash: 'def', seenAt: '2024-06-15' },
    };
    saveSeenSlides(data);
    expect(JSON.parse(localStorage.getItem(STORAGE_KEY)!)).toEqual(data);
  });

  it('round-trips with loadSeenSlides', () => {
    const data: FirstRunStorage = {
      x: { version: 3, hash: 'ghi', seenAt: '2025-01-01' },
    };
    saveSeenSlides(data);
    expect(loadSeenSlides()).toEqual(data);
  });
});

describe('isSlideUnseen', () => {
  it('returns true when slide has no record', () => {
    const slide = makeSlide({ id: 'missing' });
    expect(isSlideUnseen(slide, {})).toBe(true);
  });

  it('returns true when slide version is higher than record', () => {
    const slide = makeSlide({ id: 's1', version: 2 });
    const seen: FirstRunStorage = {
      s1: { version: 1, hash: contentHash(slide.title + slide.description), seenAt: '2024-01-01' },
    };
    expect(isSlideUnseen(slide, seen)).toBe(true);
  });

  it('returns true when hash differs (content changed)', () => {
    const slide = makeSlide({ id: 's1', version: 1, title: 'New Title' });
    const seen: FirstRunStorage = {
      s1: { version: 1, hash: contentHash('Old Title' + 'Test Description'), seenAt: '2024-01-01' },
    };
    expect(isSlideUnseen(slide, seen)).toBe(true);
  });

  it('returns false when version and hash match', () => {
    const slide = makeSlide({ id: 's1', version: 1 });
    const hash = contentHash(slide.title + slide.description);
    const seen: FirstRunStorage = {
      s1: { version: 1, hash, seenAt: '2024-01-01' },
    };
    expect(isSlideUnseen(slide, seen)).toBe(false);
  });

  it('returns false when record version is higher (downgrade scenario)', () => {
    const slide = makeSlide({ id: 's1', version: 1 });
    const hash = contentHash(slide.title + slide.description);
    const seen: FirstRunStorage = {
      s1: { version: 2, hash, seenAt: '2024-01-01' },
    };
    // version 1 is not > version 2, and hash matches — not unseen
    expect(isSlideUnseen(slide, seen)).toBe(false);
  });
});
