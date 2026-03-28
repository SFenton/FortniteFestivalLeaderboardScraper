import { describe, it, expect } from 'vitest';
import { rivalsSlides } from '../../../src/pages/rivals/firstRun';

describe('rivals first-run slides', () => {
  it('exports 3 slides', () => {
    expect(rivalsSlides).toHaveLength(3);
  });

  it('all slides have required fields', () => {
    for (const slide of rivalsSlides) {
      expect(slide.id).toBeTruthy();
      expect(slide.version).toBeGreaterThanOrEqual(1);
      expect(slide.title).toBeTruthy();
      expect(slide.description).toBeTruthy();
      expect(typeof slide.render).toBe('function');
    }
  });

  it('has unique slide ids', () => {
    const ids = rivalsSlides.map(s => s.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('no slides are gated', () => {
    for (const slide of rivalsSlides) {
      expect(slide.gate).toBeUndefined();
    }
  });
});
