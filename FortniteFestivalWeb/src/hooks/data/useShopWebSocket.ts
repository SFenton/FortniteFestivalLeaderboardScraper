/**
 * useShopWebSocket — Connects to the FST WebSocket endpoint and maintains
 * a live set of in-shop songIds. Applies incremental deltas (shop_changed)
 * and full snapshots (shop_snapshot) without requiring a full /api/songs refetch.
 */
import { useEffect, useRef, useCallback, useState } from 'react';
import type {
  WsNotificationMessage,
  ShopChangedMessage,
  ShopSnapshotMessage,
} from '@festival/core/api/serverTypes';

const RECONNECT_BASE_MS = 1_000;
const RECONNECT_MAX_MS = 30_000;

/* v8 ignore start -- WebSocket URL helper depends on browser location */
function getWsUrl(): string {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${location.host}/api/ws`;
}
/* v8 ignore stop */

export type ShopState = {
  /** Set of songIds currently in the item shop. Null until first snapshot. */
  shopSongIds: ReadonlySet<string> | null;
  /** Set of in-shop songIds whose offer expires tomorrow (UTC). */
  leavingTomorrowIds: ReadonlySet<string> | null;
  /** Whether the WebSocket is currently connected. */
  connected: boolean;
};

/**
 * Maintains a WebSocket connection to /api/ws and keeps shop state in sync.
 *
 * @param initialShopIds - Song IDs with shopUrl from the initial /api/songs fetch.
 *                         Used as the starting set before the first WS snapshot arrives.
 */
/* v8 ignore start -- WebSocket lifecycle cannot be exercised in jsdom */
export function useShopWebSocket(
  initialShopIds: ReadonlySet<string> | null,
  initialLeavingIds: ReadonlySet<string> | null = null,
): ShopState {
  const [shopSongIds, setShopSongIds] = useState<ReadonlySet<string> | null>(initialShopIds);
  const [leavingTomorrowIds, setLeavingTomorrowIds] = useState<ReadonlySet<string> | null>(initialLeavingIds);
  const [connected, setConnected] = useState(false);
  const wsRef = useRef<WebSocket | null>(null);
  const reconnectDelay = useRef(RECONNECT_BASE_MS);
  const reconnectTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const mountedRef = useRef(true);

  // Seed from initial fetch when it arrives (if WS snapshot hasn't come yet)
  useEffect(() => {
    if (initialShopIds && !shopSongIds) {
      setShopSongIds(initialShopIds);
    }
  }, [initialShopIds, shopSongIds]);

  useEffect(() => {
    if (initialLeavingIds && !leavingTomorrowIds) {
      setLeavingTomorrowIds(initialLeavingIds);
    }
  }, [initialLeavingIds, leavingTomorrowIds]);

  const handleMessage = useCallback((event: MessageEvent) => {
    try {
      const msg = JSON.parse(event.data as string) as WsNotificationMessage;
      switch (msg.type) {
        case 'shop_snapshot': {
          const snap = msg as ShopSnapshotMessage;
          setShopSongIds(new Set(snap.songIds));
          setLeavingTomorrowIds(new Set(snap.leavingTomorrow ?? []));
          break;
        }
        case 'shop_changed': {
          const delta = msg as ShopChangedMessage;
          setShopSongIds(prev => {
            const next = new Set(prev ?? []);
            for (const id of delta.removed) next.delete(id);
            for (const id of delta.added) next.add(id);
            return next;
          });
          setLeavingTomorrowIds(new Set(delta.leavingTomorrow ?? []));
          break;
        }
        // Other notification types can be handled here in the future
        default:
          break;
      }
    } catch {
      // Ignore malformed messages
    }
  }, []);

  const connect = useCallback(() => {
    if (!mountedRef.current) return;
    if (wsRef.current?.readyState === WebSocket.OPEN) return;

    const ws = new WebSocket(getWsUrl());
    wsRef.current = ws;

    ws.onopen = () => {
      if (!mountedRef.current) { ws.close(); return; }
      setConnected(true);
      reconnectDelay.current = RECONNECT_BASE_MS;
    };

    ws.onmessage = handleMessage;

    ws.onclose = () => {
      if (!mountedRef.current) return;
      setConnected(false);
      // Exponential backoff reconnect
      reconnectTimer.current = setTimeout(() => {
        reconnectDelay.current = Math.min(reconnectDelay.current * 2, RECONNECT_MAX_MS);
        connect();
      }, reconnectDelay.current);
    };

    ws.onerror = () => {
      ws.close();
    };
  }, [handleMessage]);

  useEffect(() => {
    mountedRef.current = true;
    connect();
    return () => {
      mountedRef.current = false;
      clearTimeout(reconnectTimer.current);
      wsRef.current?.close();
    };
  }, [connect]);

  return { shopSongIds, leavingTomorrowIds, connected };
}
/* v8 ignore stop */
