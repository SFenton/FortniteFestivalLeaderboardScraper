import { describe, it, expect, beforeEach } from 'vitest';
import type { ServerSong, ShopSong } from '@festival/core/api';
import {
  SHOP_SORT_STORAGE_KEY,
  defaultShopSort,
  enrichShopSongs,
  isShopSortActive,
  loadShopSort,
  saveShopSort,
  sortShopSongs,
  type SortableShopSong,
} from '../../../src/pages/shop/shopSort';

const shop = (songId: string, title: string, artist: string, year?: number): ShopSong => ({
  songId, title, artist, year, shopUrl: `https://example.com/${songId}`,
});

const SONGS: SortableShopSong[] = [
  { ...shop('b', 'Bravo', 'Zed', 2001), durationSeconds: 200 },
  { ...shop('a', 'Alpha', 'Mike', 1999), durationSeconds: 300 },
  { ...shop('c', 'Charlie', 'Alan', 2010), durationSeconds: 100 },
  { ...shop('d', 'Delta', 'Mike', 1999), durationSeconds: 300 },
];

const ids = (list: readonly SortableShopSong[]) => list.map(s => s.songId);

describe('sortShopSongs', () => {
  it('sorts by title ascending and descending', () => {
    expect(ids(sortShopSongs(SONGS, { sortMode: 'title', sortAscending: true }))).toEqual(['a', 'b', 'c', 'd']);
    expect(ids(sortShopSongs(SONGS, { sortMode: 'title', sortAscending: false }))).toEqual(['d', 'c', 'b', 'a']);
  });

  it('sorts by artist with title as the tie-breaker', () => {
    expect(ids(sortShopSongs(SONGS, { sortMode: 'artist', sortAscending: true }))).toEqual(['c', 'a', 'd', 'b']);
    expect(ids(sortShopSongs(SONGS, { sortMode: 'artist', sortAscending: false }))).toEqual(['b', 'd', 'a', 'c']);
  });

  it('sorts by year', () => {
    expect(ids(sortShopSongs(SONGS, { sortMode: 'year', sortAscending: true }))).toEqual(['a', 'd', 'b', 'c']);
    expect(ids(sortShopSongs(SONGS, { sortMode: 'year', sortAscending: false }))).toEqual(['c', 'b', 'd', 'a']);
  });

  it('sorts by duration', () => {
    expect(ids(sortShopSongs(SONGS, { sortMode: 'duration', sortAscending: true }))).toEqual(['c', 'b', 'a', 'd']);
    expect(ids(sortShopSongs(SONGS, { sortMode: 'duration', sortAscending: false }))).toEqual(['d', 'a', 'b', 'c']);
  });

  it('treats missing year/duration as zero, like the Songs list', () => {
    const list: SortableShopSong[] = [shop('x', 'X', 'A', 2020), shop('y', 'Y', 'A')];
    expect(ids(sortShopSongs(list, { sortMode: 'year', sortAscending: true }))).toEqual(['y', 'x']);
    expect(ids(sortShopSongs(list, { sortMode: 'duration', sortAscending: true }))).toEqual(['x', 'y']);
  });

  it('does not mutate the input', () => {
    const copy = [...SONGS];
    sortShopSongs(SONGS, { sortMode: 'duration', sortAscending: false });
    expect(SONGS).toEqual(copy);
  });
});

describe('enrichShopSongs', () => {
  const catalog = [
    { songId: 'a', title: 'Alpha', artist: 'Mike', year: 1998, durationSeconds: 245 },
    { songId: 'b', title: 'Bravo', artist: 'Zed', year: 2001, durationSeconds: 180 },
  ] as ServerSong[];

  it('adds catalogue duration and fills a missing year', () => {
    const result = enrichShopSongs([shop('a', 'Alpha', 'Mike', 1999), shop('b', 'Bravo', 'Zed')], catalog);
    expect(result[0]).toMatchObject({ songId: 'a', year: 1999, durationSeconds: 245 });
    expect(result[1]).toMatchObject({ songId: 'b', year: 2001, durationSeconds: 180 });
  });

  it('leaves songs missing from the catalogue unchanged', () => {
    const entry = shop('z', 'Zulu', 'Q', 2000);
    expect(enrichShopSongs([entry], catalog)[0]).toBe(entry);
    expect(enrichShopSongs([entry], [])[0]).toBe(entry);
  });
});

describe('shop sort persistence', () => {
  beforeEach(() => localStorage.clear());

  it('defaults to title ascending', () => {
    expect(loadShopSort()).toEqual({ sortMode: 'title', sortAscending: true });
    expect(isShopSortActive(defaultShopSort())).toBe(false);
  });

  it('round-trips a saved choice', () => {
    saveShopSort({ sortMode: 'duration', sortAscending: false });
    expect(loadShopSort()).toEqual({ sortMode: 'duration', sortAscending: false });
    expect(isShopSortActive(loadShopSort())).toBe(true);
  });

  it('flags a non-default direction as active', () => {
    expect(isShopSortActive({ sortMode: 'title', sortAscending: false })).toBe(true);
  });

  it('falls back to defaults for invalid or corrupt values', () => {
    localStorage.setItem(SHOP_SORT_STORAGE_KEY, JSON.stringify({ sortMode: 'hasfc', sortAscending: 'yes' }));
    expect(loadShopSort()).toEqual(defaultShopSort());
    localStorage.setItem(SHOP_SORT_STORAGE_KEY, '{not json');
    expect(loadShopSort()).toEqual(defaultShopSort());
    localStorage.setItem(SHOP_SORT_STORAGE_KEY, 'null');
    expect(loadShopSort()).toEqual(defaultShopSort());
  });
});
