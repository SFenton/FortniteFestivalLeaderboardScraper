import { describe, it, expect } from 'vitest';
import { shopSlides } from '../../../src/pages/shop/firstRun';

describe('shop first-run slides', () => {
  it('exports 4 slides', () => {
    expect(shopSlides).toHaveLength(4);
  });

  it('all slides have required fields', () => {
    for (const slide of shopSlides) {
      expect(slide.id).toBeTruthy();
      expect(slide.version).toBeGreaterThanOrEqual(1);
      expect(slide.title).toBeTruthy();
      expect(slide.description).toBeTruthy();
      expect(typeof slide.render).toBe('function');
    }
  });

  it('has unique slide ids', () => {
    const ids = shopSlides.map(s => s.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('shop-highlighting slide is gated on shopHighlightEnabled', () => {
    const highlighting = shopSlides.find(s => s.id === 'shop-highlighting');
    expect(highlighting).toBeDefined();
    expect(highlighting!.gate).toBeDefined();
    expect(highlighting!.gate!({ hasPlayer: false, shopHighlightEnabled: false })).toBe(false);
    expect(highlighting!.gate!({ hasPlayer: false, shopHighlightEnabled: true })).toBe(true);
  });
});
