/**
 * Item Shop sort modes, persistence, and comparator.
 * Mirrors the general (non-instrument) Songs sort modes and comparison rules.
 */
import type { ServerSong as Song, ShopSong } from '@festival/core/api';

export type ShopSortMode = 'title' | 'artist' | 'year' | 'duration';

export const SHOP_SORT_MODES: readonly ShopSortMode[] = ['title', 'artist', 'year', 'duration'];

export type ShopSortSettings = {
  sortMode: ShopSortMode;
  sortAscending: boolean;
};

/** A shop entry enriched with catalogue metadata needed for sorting and display. */
export type SortableShopSong = ShopSong & { durationSeconds?: number };

export const SHOP_SORT_STORAGE_KEY = 'fst:shopSort';

export function defaultShopSort(): ShopSortSettings {
  return { sortMode: 'title', sortAscending: true };
}

export function isShopSortActive(settings: ShopSortSettings): boolean {
  const d = defaultShopSort();
  return settings.sortMode !== d.sortMode || settings.sortAscending !== d.sortAscending;
}

function isShopSortMode(value: unknown): value is ShopSortMode {
  return typeof value === 'string' && (SHOP_SORT_MODES as readonly string[]).includes(value);
}

export function loadShopSort(): ShopSortSettings {
  const defaults = defaultShopSort();
  try {
    const raw = localStorage.getItem(SHOP_SORT_STORAGE_KEY);
    if (!raw) return defaults;
    const parsed = JSON.parse(raw) as Partial<ShopSortSettings> | null;
    return {
      sortMode: isShopSortMode(parsed?.sortMode) ? parsed.sortMode : defaults.sortMode,
      sortAscending: typeof parsed?.sortAscending === 'boolean' ? parsed.sortAscending : defaults.sortAscending,
    };
  } catch {
    return defaults;
  }
}

export function saveShopSort(settings: ShopSortSettings): void {
  try {
    localStorage.setItem(SHOP_SORT_STORAGE_KEY, JSON.stringify({ sortMode: settings.sortMode, sortAscending: settings.sortAscending }));
  } catch { /* storage unavailable */ }
}

/** Fill in year/duration from the song catalogue; the shop feed carries no duration. */
export function enrichShopSongs(shopSongs: readonly ShopSong[], catalog: readonly Song[]): SortableShopSong[] {
  if (catalog.length === 0) return [...shopSongs];
  const byId = new Map(catalog.map(s => [s.songId, s]));
  return shopSongs.map(shopSong => {
    const song = byId.get(shopSong.songId);
    if (!song) return shopSong;
    return {
      ...shopSong,
      year: shopSong.year ?? song.year,
      durationSeconds: song.durationSeconds,
    };
  });
}

export function sortShopSongs<T extends SortableShopSong>(songs: readonly T[], { sortMode, sortAscending }: ShopSortSettings): T[] {
  const dir = sortAscending ? 1 : -1;
  return [...songs].sort((a, b) => {
    let cmp: number;
    switch (sortMode) {
      case 'artist':
        cmp = a.artist.localeCompare(b.artist); break;
      case 'year':
        cmp = (a.year ?? 0) - (b.year ?? 0); break;
      case 'duration':
        cmp = (a.durationSeconds ?? 0) - (b.durationSeconds ?? 0); break;
      default:
        cmp = a.title.localeCompare(b.title); break;
    }
    return cmp === 0 ? a.title.localeCompare(b.title) * dir : cmp * dir;
  });
}
