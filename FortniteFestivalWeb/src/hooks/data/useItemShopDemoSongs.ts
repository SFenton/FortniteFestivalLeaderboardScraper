/**
 * Hook for first-run demo slides that display item shop songs.
 *
 * Returns real item shop songs when available (via ShopContext),
 * falling back to random catalog songs with album art when the
 * shop is empty or hasn't loaded yet.
 */
import { useMemo } from 'react';
import type { ServerSong as Song } from '@festival/core/api/serverTypes';
import { useShop } from '../../contexts/ShopContext';
import { useFestival } from '../../contexts/FestivalContext';
import { shuffle } from './useDemoSongs';

export interface UseItemShopDemoSongsResult {
  /** Songs to display in the demo (real shop songs or catalog fallback). */
  songs: Song[];
  /** True when songs are live item shop data (not fallback). */
  isLive: boolean;
}

export function useItemShopDemoSongs(maxCount: number): UseItemShopDemoSongsResult {
  const { shopSongs } = useShop();
  const { state: { songs: allSongs } } = useFestival();

  return useMemo(() => {
    if (shopSongs.length > 0) {
      const pool = shuffle(shopSongs);
      return { songs: pool.slice(0, maxCount), isLive: true };
    }
    // Fallback: random songs with album art from the catalog
    const withArt = allSongs.filter(s => s.albumArt);
    const pool = shuffle(withArt);
    return { songs: pool.slice(0, maxCount), isLive: false };
  }, [shopSongs, allSongs, maxCount]);
}
