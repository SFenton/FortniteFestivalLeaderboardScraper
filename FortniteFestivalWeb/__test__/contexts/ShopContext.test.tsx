/**
 * Tests for ShopContext and useShopState.
 *
 * ShopContext fetches /api/shop, wraps useShopWebSocket, and provides
 * shopSongIds, shopSongs (ShopSong[]), getShopUrl, connected.
 * useShopState layers settings on top.
 */
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ShopSong } from '@festival/core/api';

// Mock shop songs map for WS
const mockShopSongsMap = vi.hoisted(() => new Map<string, ShopSong>([
  ['song-1', { songId: 'song-1', title: 'Song One', artist: 'Artist 1', shopUrl: 'https://shop/1', isNew: true }],
  ['song-3', { songId: 'song-3', title: 'Song Three', artist: 'Artist 3', shopUrl: 'https://shop/3' }],
]));

const mockShopState = vi.hoisted(() => ({
  shopSongIds: new Set(['song-1', 'song-3']) as ReadonlySet<string>,
  shopSongsMap: mockShopSongsMap as ReadonlyMap<string, ShopSong>,
  connected: true,
  leavingTomorrowIds: null as ReadonlySet<string> | null,
  newShopIds: new Set(['song-1']) as ReadonlySet<string>,
}));
const mockUseInitialQueryData = vi.hoisted(() => ({ value: false }));

vi.mock('../../src/hooks/data/useShopWebSocket', () => ({
  useShopWebSocket: (
    initialShopIds: ReadonlySet<string> | null,
    initialLeavingIds: ReadonlySet<string> | null,
    initialNewIds: ReadonlySet<string> | null,
  ) => mockUseInitialQueryData.value
    ? {
        shopSongIds: initialShopIds,
        leavingTomorrowIds: initialLeavingIds,
        newShopIds: initialNewIds,
        shopSongsMap: null,
        connected: false,
      }
    : mockShopState,
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

const mockGetShop = vi.hoisted(() => vi.fn());

vi.mock('../../src/api/client', () => ({
  api: {
    getShop: mockGetShop,
  },
}));

import { ShopProvider, useShop } from '../../src/contexts/ShopContext';
import { useShopState } from '../../src/hooks/data/useShopState';
import { SettingsProvider } from '../../src/contexts/SettingsContext';

let testQc: QueryClient;

function shopWrapper({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={testQc}>
      <ShopProvider>{children}</ShopProvider>
    </QueryClientProvider>
  );
}

function fullWrapper({ children }: { children: ReactNode }) {
  return (
    <QueryClientProvider client={testQc}>
      <SettingsProvider>
        <ShopProvider>{children}</ShopProvider>
      </SettingsProvider>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  vi.clearAllMocks();
  localStorage.clear();
  testQc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  mockGetShop.mockReturnValue(new Promise(() => {}));
  mockUseInitialQueryData.value = false;
  mockShopState.shopSongIds = new Set(['song-1', 'song-3']);
  mockShopState.shopSongsMap = mockShopSongsMap;
  mockShopState.connected = true;
  mockShopState.leavingTomorrowIds = null;
  mockShopState.newShopIds = new Set(['song-1']);
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
    expect(result.current.shopSongs.find(s => s.songId === 'song-1')?.isNew).toBe(true);
  });

  it('provides newShopIds', () => {
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    expect(result.current.newShopIds?.has('song-1')).toBe(true);
    expect(result.current.newShopIds?.has('song-3')).toBe(false);
  });

  it('returns empty shopSongs when shopSongsMap is null', () => {
    mockShopState.shopSongIds = null as unknown as ReadonlySet<string>;
    mockShopState.shopSongsMap = null as unknown as ReadonlyMap<string, ShopSong>;
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    expect(result.current.shopSongs).toEqual([]);
  });

  it('uses one shared query request across concurrent providers', async () => {
    let resolveRequest!: (value: { songs: ShopSong[] }) => void;
    mockUseInitialQueryData.value = true;
    mockGetShop.mockReturnValue(new Promise(resolve => {
      resolveRequest = resolve;
    }));

    const first = renderHook(() => useShop(), { wrapper: shopWrapper });
    const second = renderHook(() => useShop(), { wrapper: shopWrapper });

    expect(mockGetShop).toHaveBeenCalledTimes(1);
    resolveRequest({
      songs: [{ songId: 'shared', title: 'Shared', artist: 'Artist', shopUrl: '/shared' }],
    });
    await waitFor(() => expect(first.result.current.shopSongs[0]?.songId).toBe('shared'));
    expect(second.result.current.shopSongs[0]?.songId).toBe('shared');
  });

  it('aborts the shared Shop request when its final observer unmounts', async () => {
    let requestSignal: AbortSignal | undefined;
    mockGetShop.mockImplementation(({ signal }: { signal: AbortSignal }) => {
      requestSignal = signal;
      return new Promise((_resolve, reject) => {
        signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
      });
    });

    const view = renderHook(() => useShop(), { wrapper: shopWrapper });
    await waitFor(() => expect(requestSignal).toBeDefined());

    view.unmount();

    expect(requestSignal?.aborted).toBe(true);
  });

  it('applies invalidated HTTP data while WebSocket data is unavailable', async () => {
    mockUseInitialQueryData.value = true;
    mockGetShop
      .mockResolvedValueOnce({
        songs: [{ songId: 'song-1', title: 'Song One', artist: 'Artist', shopUrl: '/one' }],
      })
      .mockResolvedValueOnce({
        songs: [{
          songId: 'song-2',
          title: 'Song Two',
          artist: 'Artist',
          shopUrl: '/two',
          leavingTomorrow: true,
          isNew: true,
        }],
      });
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    await waitFor(() => expect(result.current.shopSongs[0]?.songId).toBe('song-1'));

    await act(async () => {
      await testQc.invalidateQueries({ queryKey: ['shop', 'public'] });
    });

    await waitFor(() => expect(result.current.shopSongs[0]?.songId).toBe('song-2'));
    expect(result.current.leavingTomorrowIds?.has('song-2')).toBe(true);
    expect(result.current.newShopIds?.has('song-2')).toBe(true);
    expect(mockGetShop).toHaveBeenCalledTimes(2);
  });

  it('preserves cached Shop data when an invalidated refetch fails', async () => {
    mockUseInitialQueryData.value = true;
    mockGetShop
      .mockResolvedValueOnce({
        songs: [{ songId: 'cached', title: 'Cached', artist: 'Artist', shopUrl: '/cached' }],
      })
      .mockRejectedValueOnce(new Error('Offline'));
    const { result } = renderHook(() => useShop(), { wrapper: shopWrapper });
    await waitFor(() => expect(result.current.shopSongs[0]?.songId).toBe('cached'));

    await act(async () => {
      await testQc.invalidateQueries({ queryKey: ['shop', 'public'] });
    });

    expect(result.current.shopSongs[0]?.songId).toBe('cached');
    expect(mockGetShop).toHaveBeenCalledTimes(2);
  });

  it('shares profile-invariant Shop data across profile switches without refetching', async () => {
    mockUseInitialQueryData.value = true;
    mockGetShop.mockResolvedValue({
      songs: [{ songId: 'shared', title: 'Shared', artist: 'Artist', shopUrl: '/shared' }],
    });
    const first = renderHook(() => useShop(), { wrapper: shopWrapper });
    await waitFor(() => expect(first.result.current.shopSongs[0]?.songId).toBe('shared'));
    first.unmount();

    localStorage.setItem('fst:selectedProfile', JSON.stringify({
      type: 'player',
      accountId: 'profile-b',
      displayName: 'Profile B',
    }));
    localStorage.setItem('fst:trackedPlayer', JSON.stringify({
      accountId: 'profile-b',
      displayName: 'Profile B',
    }));
    window.dispatchEvent(new Event('fst:selectedProfileChanged'));
    const second = renderHook(() => useShop(), { wrapper: shopWrapper });

    expect(second.result.current.shopSongs[0]?.songId).toBe('shared');
    expect(mockGetShop).toHaveBeenCalledTimes(1);
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
    expect(result.current.isShopNew('song-1')).toBe(false);
  });

  it('reports isShopNew for new shop songs', () => {
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.isShopNew('song-1')).toBe(true);
    expect(result.current.isShopNew('song-3')).toBe(false);
  });

  it('reports leaving-tomorrow state from the shared Shop owner', () => {
    mockShopState.leavingTomorrowIds = new Set(['song-3']);
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.isLeavingTomorrow('song-3')).toBe(true);
    expect(result.current.isLeavingTomorrow('song-1')).toBe(false);
  });

  it('disables highlighting when hideItemShop is true', () => {
    localStorage.setItem('fst:appSettings', JSON.stringify({ hideItemShop: true }));
    const { result } = renderHook(() => useShopState(), { wrapper: fullWrapper });
    expect(result.current.isShopHighlighted('song-1')).toBe(false);
    expect(result.current.isShopNew('song-1')).toBe(false);
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
