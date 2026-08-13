import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, render, screen } from '@testing-library/react';
import LeavingTomorrowDemo from '../../../../../src/pages/songs/firstRun/demo/LeavingTomorrowDemo';
import NewInShopDemo from '../../../../../src/pages/songs/firstRun/demo/NewInShopDemo';
import ShopHighlightDemo from '../../../../../src/pages/songs/firstRun/demo/ShopHighlightDemo';

const mocks = vi.hoisted(() => ({
  isMobile: false,
  slideHeight: 300,
  songs: [
    { songId: 'leaving', title: 'Leaving Song', artist: 'Artist', year: 2024, albumArt: '/leaving.png' },
    { songId: 'shop', title: 'Shop Song', artist: 'Artist', year: 2023, albumArt: '/shop.png' },
    { songId: 'outside', title: 'Outside Song', artist: 'Artist', year: 2022, albumArt: '/outside.png' },
    { songId: 'outside-2', title: 'Other Song', artist: 'Artist', year: 2021, albumArt: '/other.png' },
  ],
  shopSongIds: new Set(['leaving', 'shop']),
  leavingTomorrowIds: new Set(['leaving']),
  newShopIds: new Set(['leaving']),
}));

vi.mock('../../../../../src/hooks/ui/useIsMobile', () => ({
  useIsMobile: () => mocks.isMobile,
}));
vi.mock('../../../../../src/contexts/FestivalContext', () => ({
  useFestival: () => ({ state: { songs: mocks.songs } }),
}));
vi.mock('../../../../../src/contexts/ShopContext', () => ({
  useShop: () => ({
    shopSongIds: mocks.shopSongIds,
    leavingTomorrowIds: mocks.leavingTomorrowIds,
    newShopIds: mocks.newShopIds,
  }),
}));
vi.mock('../../../../../src/firstRun/SlideHeightContext', () => ({
  useSlideHeight: () => mocks.slideHeight,
}));

describe('shop first-run demos', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    mocks.isMobile = false;
    mocks.slideHeight = 300;
    mocks.shopSongIds = new Set(['leaving', 'shop']);
    mocks.leavingTomorrowIds = new Set(['leaving']);
    mocks.newShopIds = new Set(['leaving']);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it('renders leaving, shop, and fallback rows and retires stagger styles', () => {
    const { container } = render(<LeavingTomorrowDemo />);
    expect(screen.getByText('Leaving Song')).toBeInTheDocument();
    expect(screen.getByText('Shop Song')).toBeInTheDocument();
    expect(screen.getByText(/demo data may be simulated/i)).toBeInTheDocument();
    expect(container.querySelector('[class*="shopHighlightRed"]')).toBeTruthy();

    act(() => vi.runAllTimers());

    expect(screen.getByText('Leaving Song')).toBeVisible();
    expect(container.querySelector('[style*="animation: fadeInUp"]')).toBeNull();
  });

  it('cycles new, existing-shop, and non-shop presentation on mobile', () => {
    mocks.isMobile = true;
    const { container } = render(<NewInShopDemo />);
    expect(screen.getByText('Leaving Song')).toBeInTheDocument();
    expect(screen.getByText('Shop Song')).toBeInTheDocument();
    expect(screen.getByText('Outside Song')).toBeInTheDocument();
    expect(container.querySelector('[class*="shopHighlightGold"]')).toBeTruthy();
    expect(container.querySelector('[class*="shopHighlight_"]')).toBeTruthy();

    act(() => vi.runAllTimers());

    expect(screen.getByText('Outside Song')).toBeVisible();
    expect(container.querySelector('[style*="animation: fadeInUp"]')).toBeNull();
  });

  it('alternates shop highlighting and returns null without usable shop data', () => {
    const { container, rerender } = render(<ShopHighlightDemo />);
    expect(screen.getByText('Leaving Song')).toBeInTheDocument();
    expect(screen.getByText('Outside Song')).toBeInTheDocument();
    expect(container.querySelector('[class*="shopHighlight_"]')).toBeTruthy();

    act(() => vi.runAllTimers());
    expect(screen.getByText('Leaving Song')).toBeVisible();
    expect(container.querySelector('[style*="animation: fadeInUp"]')).toBeNull();

    mocks.shopSongIds = new Set();
    rerender(<ShopHighlightDemo />);
    expect(container.innerHTML).toBe('');
  });
});
