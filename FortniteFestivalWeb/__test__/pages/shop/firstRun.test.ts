import { describe, it, expect } from 'vitest';
import { shopSlides } from '../../../src/pages/shop/firstRun';

describe('shop first-run slides', () => {
  it('includes the views slide when the view toggle is available', () => {
    expect(shopSlides({ viewToggleAvailable: true })).toHaveLength(4);
  });

  it('all slides have required fields', () => {
    for (const slide of shopSlides({ viewToggleAvailable: true })) {
      expect(slide.id).toBeTruthy();
      expect(slide.version).toBeGreaterThanOrEqual(1);
      expect(slide.title).toBeTruthy();
      expect(slide.description).toBeTruthy();
      expect(typeof slide.render).toBe('function');
    }
  });

  it('has unique slide ids', () => {
    const ids = shopSlides({ viewToggleAvailable: true }).map(s => s.id);
    expect(new Set(ids).size).toBe(ids.length);
  });

  it('omits the views slide when the config is list-only', () => {
    const ids = shopSlides({ viewToggleAvailable: false }).map(s => s.id);

    expect(ids).not.toContain('shop-views');
    expect(ids).toEqual(['shop-overview', 'shop-highlighting', 'shop-leaving-tomorrow']);
  });

  it('shop-highlighting slide is gated on shopHighlightEnabled', () => {
    const highlighting = shopSlides({ viewToggleAvailable: true }).find(s => s.id === 'shop-highlighting');
    expect(highlighting).toBeDefined();
    expect(highlighting!.gate).toBeDefined();
    expect(highlighting!.gate!({ hasPlayer: false, shopHighlightEnabled: false })).toBe(false);
    expect(highlighting!.gate!({ hasPlayer: false, shopHighlightEnabled: true })).toBe(true);
  });
});
