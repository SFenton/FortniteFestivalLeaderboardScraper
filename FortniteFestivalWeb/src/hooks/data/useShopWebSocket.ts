/**
 * useShopWebSocket — Subscribes to the shared WebSocket connection and maintains
 * a live set of in-shop songIds. Applies incremental deltas (shop_changed)
 * and full snapshots (shop_snapshot) without requiring a full /api/songs refetch.
 */
import { useEffect, useCallback, useRef, useState } from 'react';
import type {
  WsNotificationMessage,
  ShopChangedMessage,
  ShopSnapshotMessage,
  ShopSong,
} from '@festival/core/api/serverTypes';
import { expandAlbumArt } from '../../api/client';
import { useAppWebSocket } from './useAppWebSocket';

export type ShopState = {
  /** Set of songIds currently in the item shop. Null until first snapshot. */
  shopSongIds: ReadonlySet<string> | null;
  /** Set of in-shop songIds whose offer expires tomorrow (UTC). */
  leavingTomorrowIds: ReadonlySet<string> | null;
  /** Set of in-shop songIds marked New by the upstream shop API. */
  newShopIds: ReadonlySet<string> | null;
  /** Map of songId → ShopSong for enriched shop data from WS. */
  shopSongsMap: ReadonlyMap<string, ShopSong> | null;
  /** Whether the WebSocket is currently connected. */
  connected: boolean;
};

/**
 * Maintains shop state via the shared /api/ws connection.
 *
 * @param initialShopIds - Song IDs from the shared initial /api/shop query.
 *                         Used as the starting set before the first WS snapshot arrives.
 */
/* v8 ignore start -- WebSocket lifecycle cannot be exercised in jsdom */
export function useShopWebSocket(
  initialShopIds: ReadonlySet<string> | null,
  initialLeavingIds: ReadonlySet<string> | null = null,
  initialNewIds: ReadonlySet<string> | null = null,
): ShopState {
  const [shopSongIds, setShopSongIds] = useState<ReadonlySet<string> | null>(initialShopIds);
  const [leavingTomorrowIds, setLeavingTomorrowIds] = useState<ReadonlySet<string> | null>(initialLeavingIds);
  const [newShopIds, setNewShopIds] = useState<ReadonlySet<string> | null>(initialNewIds);
  const [shopSongsMap, setShopSongsMap] = useState<ReadonlyMap<string, ShopSong> | null>(null);
  const { connected, subscribe } = useAppWebSocket();
  const hasLiveShopData = useRef(false);

  // Keep query-backed state current until a WebSocket snapshot/delta takes ownership.
  useEffect(() => {
    if (!hasLiveShopData.current) {
      setShopSongIds(initialShopIds);
      setShopSongsMap(null);
    }
  }, [initialShopIds]);

  useEffect(() => {
    if (!hasLiveShopData.current) setLeavingTomorrowIds(initialLeavingIds);
  }, [initialLeavingIds]);

  useEffect(() => {
    if (!hasLiveShopData.current) setNewShopIds(initialNewIds);
  }, [initialNewIds]);

  useEffect(() => {
    if (!connected) hasLiveShopData.current = false;
  }, [connected]);

  const handleMessage = useCallback((msg: WsNotificationMessage) => {
    switch (msg.type) {
      case 'shop_snapshot': {
        hasLiveShopData.current = true;
        const snap = msg as ShopSnapshotMessage;
        expandAlbumArt(snap.songs);
        const map = new Map<string, ShopSong>();
        const ids = new Set<string>();
        for (const s of snap.songs) {
          map.set(s.songId, s);
          ids.add(s.songId);
        }
        setShopSongsMap(map);
        setShopSongIds(ids);
        setLeavingTomorrowIds(new Set(snap.leavingTomorrow ?? []));
        setNewShopIds(new Set(snap.newSongs ?? snap.songs.filter(s => s.isNew).map(s => s.songId)));
        break;
      }
      case 'shop_changed': {
        hasLiveShopData.current = true;
        const delta = msg as ShopChangedMessage;
        expandAlbumArt(delta.added);
        setShopSongsMap(prev => {
          const next = new Map(prev ?? []);
          for (const id of delta.removed) next.delete(id);
          for (const s of delta.added) next.set(s.songId, s);
          if (delta.newSongs) {
            const newIds = new Set(delta.newSongs);
            for (const [songId, song] of next.entries()) {
              const isNew = newIds.has(songId);
              if (!!song.isNew !== isNew) next.set(songId, { ...song, isNew });
            }
          }
          return next;
        });
        setShopSongIds(prev => {
          const next = new Set(prev ?? []);
          for (const id of delta.removed) next.delete(id);
          for (const s of delta.added) next.add(s.songId);
          return next;
        });
        setLeavingTomorrowIds(new Set(delta.leavingTomorrow ?? []));
        setNewShopIds(new Set(delta.newSongs ?? []));
        break;
      }
      default:
        break;
    }
  }, []);

  useEffect(() => {
    return subscribe(handleMessage);
  }, [subscribe, handleMessage]);

  return { shopSongIds, leavingTomorrowIds, newShopIds, shopSongsMap, connected };
}
/* v8 ignore stop */
