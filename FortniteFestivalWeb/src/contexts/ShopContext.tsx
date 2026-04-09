import { createContext, useContext, useMemo, useState, useEffect, type ReactNode } from 'react';
import type { ShopSong } from '@festival/core/api/serverTypes';
import { useFestival } from './FestivalContext';
import { useShopWebSocket, type ShopState } from '../hooks/data/useShopWebSocket';
import { api } from '../api/client';

type ShopContextValue = {
  /** Set of songIds currently in the item shop (null until data loaded). */
  shopSongIds: ReadonlySet<string> | null;
  /** Set of in-shop songIds whose offer expires tomorrow (UTC). */
  leavingTomorrowIds: ReadonlySet<string> | null;
  /** Whether the WebSocket is connected. */
  connected: boolean;
  /** Look up the shopUrl for a song, if it's in the shop. */
  getShopUrl: (songId: string) => string | undefined;
  /** All songs currently in the shop (enriched ShopSong objects). */
  shopSongs: ShopSong[];
};

const ShopContext = createContext<ShopContextValue | null>(null);

export function ShopProvider({ children }: { children: ReactNode }) {
  const { state: { songs } } = useFestival();

  // Fetch /api/shop as the initial data source
  const [shopData, setShopData] = useState<ShopSong[] | null>(null);

  useEffect(() => {
    let cancelled = false;
    try {
      api.getShop()
        .then((response) => {
          if (!cancelled) setShopData(response.songs);
        })
        .catch(() => { /* graceful degradation — WS will provide data */ });
    } catch {
      /* api.getShop may not exist in test environments */
    }
    return () => { cancelled = true; };
  }, []);

  // Build initial IDs from /api/shop response
  const initialShopIds = useMemo(() => {
    if (!shopData) return null;
    return new Set(shopData.map(s => s.songId)) as ReadonlySet<string>;
  }, [shopData]);

  const initialLeavingIds = useMemo(() => {
    if (!shopData) return null;
    const ids = shopData.filter(s => s.leavingTomorrow).map(s => s.songId);
    return ids.length > 0 ? new Set(ids) as ReadonlySet<string> : null;
  }, [shopData]);

  const { shopSongIds, leavingTomorrowIds, shopSongsMap, connected }: ShopState = useShopWebSocket(initialShopIds, initialLeavingIds);

  // Merge shop data: WS shopSongsMap > /api/shop response > empty
  const mergedShopSongs = useMemo((): ShopSong[] => {
    // If WS has enriched data, use it
    if (shopSongsMap && shopSongsMap.size > 0) {
      return Array.from(shopSongsMap.values());
    }
    // Fall back to /api/shop response
    if (shopData && shopSongIds) {
      return shopData.filter(s => shopSongIds.has(s.songId));
    }
    return shopData ?? [];
  }, [shopSongsMap, shopData, shopSongIds]);

  // Build shopUrl lookup from merged shop songs
  const shopUrlMap = useMemo(() => {
    const map = new Map<string, string>();
    for (const s of mergedShopSongs) {
      if (s.shopUrl) map.set(s.songId, s.shopUrl);
    }
    return map;
  }, [mergedShopSongs]);

  const getShopUrl = useMemo(() => {
    return (songId: string) => shopUrlMap.get(songId);
  }, [shopUrlMap]);

  // Also enrich shop songs with albumArt from the full catalog when available
  const enrichedShopSongs = useMemo(() => {
    if (!songs.length) return mergedShopSongs;
    const songById = new Map(songs.map(s => [s.songId, s]));
    return mergedShopSongs.map(ss => {
      const full = songById.get(ss.songId);
      return full?.albumArt && !ss.albumArt ? { ...ss, albumArt: full.albumArt } : ss;
    });
  }, [mergedShopSongs, songs]);

  const value = useMemo<ShopContextValue>(() => ({
    shopSongIds,
    leavingTomorrowIds,
    connected,
    getShopUrl,
    shopSongs: enrichedShopSongs,
  }), [shopSongIds, leavingTomorrowIds, connected, getShopUrl, enrichedShopSongs]);

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
