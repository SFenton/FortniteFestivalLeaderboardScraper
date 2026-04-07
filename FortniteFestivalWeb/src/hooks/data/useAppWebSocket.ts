/**
 * useAppWebSocket — Shared WebSocket connection to /api/ws.
 * Replaces per-feature WS hooks with a single connection that routes
 * messages to registered listeners by type.
 */
import { useEffect, useRef, useCallback, useState } from 'react';
import type { WsNotificationMessage } from '@festival/core/api/serverTypes';

const RECONNECT_BASE_MS = 1_000;
const RECONNECT_MAX_MS = 30_000;

/* v8 ignore start -- WebSocket URL helper depends on browser location */
function getWsUrl(): string {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${location.host}/api/ws`;
}
/* v8 ignore stop */

export type WsMessageHandler = (msg: WsNotificationMessage) => void;

type AppWebSocketState = {
  connected: boolean;
  subscribe: (handler: WsMessageHandler) => () => void;
  send: (data: string) => void;
};

let sharedInstance: SharedWebSocket | null = null;
let refCount = 0;

class SharedWebSocket {
  private ws: WebSocket | null = null;
  private handlers = new Set<WsMessageHandler>();
  private reconnectDelay = RECONNECT_BASE_MS;
  private reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  private alive = true;

  constructor() {
    this.connect();
  }

  subscribe(handler: WsMessageHandler): () => void {
    this.handlers.add(handler);
    return () => { this.handlers.delete(handler); };
  }

  get connected(): boolean {
    return this.ws?.readyState === WebSocket.OPEN;
  }

  send(data: string): void {
    if (this.ws?.readyState === WebSocket.OPEN) {
      this.ws.send(data);
    }
  }

  /* v8 ignore start -- WebSocket lifecycle */
  private connect() {
    if (!this.alive) return;
    if (this.ws?.readyState === WebSocket.OPEN) return;

    const ws = new WebSocket(getWsUrl());
    this.ws = ws;

    ws.onopen = () => {
      if (!this.alive) { ws.close(); return; }
      this.reconnectDelay = RECONNECT_BASE_MS;
    };

    ws.onmessage = (event: MessageEvent) => {
      try {
        const msg = JSON.parse(event.data as string) as WsNotificationMessage;
        for (const handler of this.handlers) {
          handler(msg);
        }
      } catch {
        // Ignore malformed messages
      }
    };

    ws.onclose = () => {
      if (!this.alive) return;
      this.reconnectTimer = setTimeout(() => {
        this.reconnectDelay = Math.min(this.reconnectDelay * 2, RECONNECT_MAX_MS);
        this.connect();
      }, this.reconnectDelay);
    };

    ws.onerror = () => { ws.close(); };
  }
  /* v8 ignore stop */

  destroy() {
    this.alive = false;
    clearTimeout(this.reconnectTimer);
    this.ws?.close();
    this.handlers.clear();
  }
}

/**
 * Shared WebSocket hook. All consumers share a single connection.
 * Returns `connected` state and a `subscribe` function to register message handlers.
 */
/* v8 ignore start -- WebSocket lifecycle cannot be exercised in jsdom */
export function useAppWebSocket(): AppWebSocketState {
  const [connected, setConnected] = useState(false);
  const intervalRef = useRef<ReturnType<typeof setInterval>>(undefined);

  useEffect(() => {
    refCount++;
    if (!sharedInstance) {
      sharedInstance = new SharedWebSocket();
    }
    const instance = sharedInstance;

    // Poll connected state (lightweight — just checks readyState)
    const sync = () => setConnected(instance.connected);
    sync();
    intervalRef.current = setInterval(sync, 1_000);

    return () => {
      clearInterval(intervalRef.current);
      refCount--;
      if (refCount === 0 && sharedInstance) {
        sharedInstance.destroy();
        sharedInstance = null;
      }
    };
  }, []);

  const subscribe = useCallback((handler: WsMessageHandler) => {
    if (!sharedInstance) return () => {};
    return sharedInstance.subscribe(handler);
  }, []);

  const send = useCallback((data: string) => {
    sharedInstance?.send(data);
  }, []);

  return { connected, subscribe, send };
}
/* v8 ignore stop */
