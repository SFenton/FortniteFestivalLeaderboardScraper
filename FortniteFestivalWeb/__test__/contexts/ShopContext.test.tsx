/**
 * Tests for ShopContext and useShopState.
 *
 * ShopContext fetches /api/shop, wraps useShopWebSocket, and provides
 * shopSongIds, shopSongs (ShopSong[]), getShopUrl, connected.
 * useShopState layers settings on top.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { renderHook, act, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import type { ShopSong } from '@festival/core/api/serverTypes';

// Mock shop songs map for WS
const mockShopSongsMap = vi.hoisted(() => new Map<string, ShopSong>([
  ['song-1', { songId: 'song-1', title: 'Song One', artist: 'Artist 1', shopUrl: 'https://shop/1' }],
  ['song-3', { songId: 'song-3', title: 'Song Three', artist: 'Artist 3', shopUrl: 'https://shop/3' }],
]));

const mockShopState = vi.hoisted(() => ({
  shopSongIds: new Set(['song-1', 'song-3']) as ReadonlySet<string>,
  shopSongsMap: mockShopSongsMap as ReadonlyMap<string, ShopSong>,
  connected: true,
  leavingTomorrowIds: null as ReadonlySet<string> | null,
}));

vi.mock('../../src/hooks/data/useShopWebSocket', () => ({
  useShopWebSocket: () => mockShopState,
}));

// Mock FestivalContext (songs no longer have shopUrl)
const mockSongs = vi.hoisted(() => [
  { songId: 'song-1', title: 'Song One', artist: 'Artist 1', albumArt: 'art1.jpg' },
  { songId: 'song-2', title: 'Song Two', artist: 'Artist 2' },
  { songId: 'song-3', title: 'Song Three', artist: 'Artist 3', albumArt: 'art3.jpg' },
]);

vi.mock('../../src/contexts/FestivalContext', () => ({
  useFestival: () => ({ state: { songs: mockSongs } }),
}));

// Mock FeatureFlagsContext
vi.mock('../../src/contexts/FeatureFlagsContext', () => ({
  useFeatureFlags: () => ({ rivals: true, compete: true, leaderboards: true }),
}));

// Mock api.getShop
vi.mock('../../src/api/client', () => ({
  api: {
    getShop: vi.fn().mockResolvedValue({
      songs: [
        { songId: 'song-1', title: 'Song One', artist: 'Artist 1', shopUrl: 'https://shop/1' },
        { songId: 'song-3', title: 'Song Three', artist: 'Artist 3', shopUrl: 'https://shop/3' },
      ],
    }),
  },
}));

import { ShopProvider, useShop } from '../../src/contexts/ShopContext';
import { useShopState } from '../../src/hooks/data/useShopState';
import { SettingsProvider } from '../../src/contexts/SettingsContext';

function shopWrapper({ children }: { children: ReactNode }) {
  return <ShopProvider>{children}</ShopProvider>;
}

function fullWrapper({ children }: { children: ReactNode }) {
  return (
    <SettingsProvider>
      <ShopProvider>{children}</ShopProvider>
    </SettingsProvider>
  );
}

beforeEach(() => {
  localStorage.clear();
  mockShopState.shopSongIds = new Set(['song-1', 'song-3']);
  mockShopState.shopSongsMap = mockShopSongsMap;
  mockShopState.connected = true;
});

describe('ShopContext', () => {
  it('provides shopSongIds', () => {
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    expect(result.current.shopSongIds).toBeDefined();
    expect(result.current.shopSongIds!.has('song-1')).toBe(true);
  });

  it('provides connected state', () => {
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    expect(result.current.connected).toBe(true);
  });

  it('provides getShopUrl for songs in the shop', () => {
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    // shopUrl comes from ShopSong via WS shopSongsMap
    expect(result.current.getShopUrl('song-1')).toBe('https://shop/1');
    expect(result.current.getShopUrl('song-2')).toBeUndefined();
  });

  it('provides shopSongs from WS enriched data', () => {
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    expect(result.current.shopSongs.length).toBe(2);
    expect(result.current.shopSongs.map(s => s.songId)).toEqual(['song-1', 'song-3']);
  });

  it('returns empty shopSongs when shopSongsMap is null', () => {
    mockShopState.shopSongIds = null as unknown as ReadonlySet<string>;
    mockShopState.shopSongsMap = null as unknown as ReadonlyMap<string, ShopSong>;
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    expect(result.current.shopSongs).toEqual([]);
  });

  it('throws when used outside provider', () => {
    expect(() => {
      renderHook(() => useShop());
    }).toThrow('useShop must be used within a ShopProvider');
  });
});

describe('useShopState', () => {
  it('reports isShopHighlighted for shop songs', () => {
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.isShopHighlighted('song-1')).toBe(true);
    expect(result.current.isShopHighlighted('song-2')).toBe(false);
  });

  it('reports isInShop regardless of highlighting setting', () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({ disableShopHighlighting: true }));
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.isInShop('song-1')).toBe(true);
    expect(result.current.isShopHighlighted('song-1')).toBe(false);
  });

  it('disables highlighting when hideItemShop is true', () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({ hideItemShop: true }));
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.isShopHighlighted('song-1')).toBe(false);
    expect(result.current.isShopVisible).toBe(false);
  });

  it('returns empty shopSongs when shop is hidden', () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({ hideItemShop: true }));
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.shopSongs).toEqual([]);
  });

  it('returns shopSongs when shop is visible', () => {
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.shopSongs.length).toBe(2);
    expect(result.current.isShopVisible).toBe(true);
  });

  it('provides getShopUrl passthrough', () => {
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.getShopUrl('song-1')).toBe('https://shop/1');
  });

  it('provides connected passthrough', () => {
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.connected).toBe(true);
  });

  it('returns false for isShopHighlighted when shopSongIds is null', () => {
    mockShopState.shopSongIds = null as unknown as ReadonlySet<string>;
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.isShopHighlighted('song-1')).toBe(false);
  });

  it('returns false for isInShop when shopSongIds is null', () => {
    mockShopState.shopSongIds = null as unknown as ReadonlySet<string>;
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.isInShop('song-1')).toBe(false);
  });
});
