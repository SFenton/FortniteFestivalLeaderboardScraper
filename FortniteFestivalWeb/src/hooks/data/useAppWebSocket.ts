/**
 * useAppWebSocket — Shared WebSocket connection to /api/ws.
 * Replaces per-feature WS hooks with a single connection that routes
 * messages to registered listeners by type.
 */
import { useEffect, useRef, useCallback, useState } from 'react';
import type { WsNotificationMessage } from '@festival/core/api/serverTypes';

const RECONNECT_BASE_MS = 1_000;
const RECONNECT_MAX_MS = 30_000;
/** Grace period before tearing down the shared socket when all consumers unmount (ms). */
const DESTROY_DEFER_MS = 300;

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
let destroyTimer: ReturnType<typeof setTimeout> | undefined;

class SharedWebSocket {
  private ws: WebSocket | null = null;
  private handlers = new Set<WsMessageHandler>();
  private reconnectDelay = RECONNECT_BASE_MS;
  private reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  private alive = true;
  /** Monotonic version so stale callbacks from a previous WebSocket are ignored. */
  private connectVersion = 0;

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
    // Guard: don't create a second socket while one is CONNECTING or OPEN
    if (this.ws && (this.ws.readyState === WebSocket.CONNECTING || this.ws.readyState === WebSocket.OPEN)) return;

    const version = ++this.connectVersion;
    const ws = new WebSocket(getWsUrl());
    this.ws = ws;

    ws.onopen = () => {
      // Stale callback — a newer connect() superseded this one
      if (version !== this.connectVersion) { ws.close(); return; }
      if (!this.alive) { ws.close(); return; }
      this.reconnectDelay = RECONNECT_BASE_MS;
    };

    ws.onmessage = (event: MessageEvent) => {
      if (version !== this.connectVersion) return;
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
      if (version !== this.connectVersion) return;
      if (!this.alive) return;
      this.clearReconnectTimer();
      this.reconnectTimer = setTimeout(() => {
        this.reconnectDelay = Math.min(this.reconnectDelay * 2, RECONNECT_MAX_MS);
        this.connect();
      }, this.reconnectDelay);
    };

    ws.onerror = () => {
      if (version !== this.connectVersion) return;
      ws.close();
    };
  }
  /* v8 ignore stop */

  private clearReconnectTimer() {
    if (this.reconnectTimer !== undefined) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = undefined;
    }
  }

  destroy() {
    this.alive = false;
    this.clearReconnectTimer();
    // Avoid closing while CONNECTING — bump version so callbacks no-op,
    // then let the browser finish the handshake naturally; onopen/onclose
    // will see the stale version and silently discard.
    if (this.ws) {
      if (this.ws.readyState === WebSocket.OPEN || this.ws.readyState === WebSocket.CLOSING) {
        this.ws.close();
      } else {
        // CONNECTING — mark version stale so lifecycle handlers ignore it.
        // The socket will close on its own or via the stale-version guard in onopen.
        this.connectVersion++;
      }
      this.ws = null;
    }
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
    // Cancel any pending deferred destroy (StrictMode remount / quick route switch)
    if (destroyTimer !== undefined) {
      clearTimeout(destroyTimer);
      destroyTimer = undefined;
    }

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
        // Defer destroy so transient unmount/remount cycles (StrictMode,
        // route transitions) don't tear down and recreate the socket.
        const inst = sharedInstance;
        destroyTimer = setTimeout(() => {
          destroyTimer = undefined;
          // Re-check: a new consumer may have mounted during the window
          if (refCount === 0 && sharedInstance === inst) {
            sharedInstance.destroy();
            sharedInstance = null;
          }
        }, DESTROY_DEFER_MS);
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
