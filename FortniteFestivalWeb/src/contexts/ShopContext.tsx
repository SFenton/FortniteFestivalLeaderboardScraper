import { createContext, useContext, useMemo, type ReactNode } from 'react';
import type { ServerSong as Song } from '@festival/core/api/serverTypes';
import { useFestival } from './FestivalContext';
import { useShopWebSocket, type ShopState } from '../hooks/data/useShopWebSocket';

type ShopContextValue = {
  /** Set of songIds currently in the item shop (null until data loaded). */
  shopSongIds: ReadonlySet<string> | null;
  /** Whether the WebSocket is connected. */
  connected: boolean;
  /** Look up the shopUrl for a song, if it's in the shop. */
  getShopUrl: (songId: string) => string | undefined;
  /** All songs currently in the shop. */
  shopSongs: Song[];
};

const ShopContext = createContext<ShopContextValue | null>(null);

export function ShopProvider({ children }: { children: ReactNode }) {
  const { state: { songs } } = useFestival();

  // Build initial shop IDs from songs that have a shopUrl
  const initialShopIds = useMemo(() => {
    const ids = songs.filter(s => s.shopUrl).map(s => s.songId);
    return ids.length > 0 ? new Set(ids) as ReadonlySet<string> : null;
  }, [songs]);

  const { shopSongIds, connected }: ShopState = useShopWebSocket(initialShopIds);

  // Build shopUrl lookup from Songs data
  const shopUrlMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const s of songs) {
      if (s.shopUrl) map.set(s.songId, s.shopUrl);
    }
    return map;
  }, [songs]);

  const getShopUrl = useMemo(() => {
    return (songId: string) => shopUrlMap.get(songId);
  }, [shopUrlMap]);

  const shopSongs = useMemo(() => {
    if (!shopSongIds) return [];
    return songs.filter(s => shopSongIds.has(s.songId));
  }, [songs, shopSongIds]);

  const value = useMemo<ShopContextValue>(() => ({
    shopSongIds,
    connected,
    getShopUrl,
    shopSongs,
  }), [shopSongIds, connected, getShopUrl, shopSongs]);

  return (
    <ShopContext.Provider value={value}>
      {children}
    </ShopContext.Provider>
  );
}

export function useShop(): ShopContextValue {
  const ctx = useContext(ShopContext);
  if (!ctx) {
    throw new Error('useShop must be used within a ShopProvider');
  }
  return ctx;
}
