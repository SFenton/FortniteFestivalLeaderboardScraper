/**
 * useAppWebSocket — Shared WebSocket connection to /api/ws.
 * Replaces per-feature WS hooks with a single connection that routes
 * messages to registered listeners by type.
 */
import { useEffect, useCallback, useState } from 'react';
import type { WsNotificationMessage } from '@festival/core/api/serverTypes';

const RECONNECT_BASE_MS = 1_000;
const RECONNECT_MAX_MS = 30_000;
const RESUME_RECONNECT_THRESHOLD_MS = 5_000;
const RESUME_RECOVERY_DEBOUNCE_MS = 1_000;
/** Grace period before tearing down the shared socket when all consumers unmount (ms). */
const DESTROY_DEFER_MS = 300;

/* v8 ignore start -- WebSocket URL helper depends on browser location */
function getWsUrl(): string {
  const proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
  return `${proto}//${location.host}/api/ws`;
}
/* v8 ignore stop */

export type WsMessageHandler = (msg: WsNotificationMessage) => void;
type ConnectionStateHandler = (connected: boolean) => void;
type OpenHandler = () => void;

type AppWebSocketState = {
  connected: boolean;
  subscribe: (handler: WsMessageHandler) => () => void;
  send: (data: string) => void;
  subscribeOpen: (handler: OpenHandler) => () => void;
};

let sharedInstance: SharedWebSocket | null = null;
let refCount = 0;
let destroyTimer: ReturnType<typeof setTimeout> | undefined;

class SharedWebSocket {
  private ws: WebSocket | null = null;
  private handlers = new Set<WsMessageHandler>();
  private connectionHandlers = new Set<ConnectionStateHandler>();
  private openHandlers = new Set<OpenHandler>();
  private reconnectDelay = RECONNECT_BASE_MS;
  private reconnectTimer: ReturnType<typeof setTimeout> | undefined;
  private alive = true;
  /** Monotonic version so stale callbacks from a previous WebSocket are ignored. */
  private connectVersion = 0;
  private connectedState = false;
  private hiddenAt: number | null = null;
  private hiddenEpoch = 0;
  private recoveredHiddenEpoch = 0;
  private lastResumeRecoveryAt = 0;

  constructor() {
    this.bindBrowserLifecycle();
    this.connect();
  }

  subscribe(handler: WsMessageHandler): () => void {
    this.handlers.add(handler);
    return () => { this.handlers.delete(handler); };
  }

  subscribeConnection(handler: ConnectionStateHandler): () => void {
    this.connectionHandlers.add(handler);
    handler(this.connectedState);
    return () => { this.connectionHandlers.delete(handler); };
  }

  subscribeOpen(handler: OpenHandler): () => void {
    this.openHandlers.add(handler);
    return () => { this.openHandlers.delete(handler); };
  }

  get connected(): boolean {
    return this.connectedState;
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

    this.clearReconnectTimer();

    const version = ++this.connectVersion;
    const ws = new WebSocket(getWsUrl());
    this.ws = ws;

    ws.onopen = () => {
      // Stale callback — a newer connect() superseded this one
      if (version !== this.connectVersion) { ws.close(); return; }
      if (!this.alive) { ws.close(); return; }
      this.reconnectDelay = RECONNECT_BASE_MS;
      this.hiddenAt = null;
      this.setConnected(true);
      this.notifyOpen();
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
      if (this.ws === ws) {
        this.ws = null;
      }
      this.setConnected(false);
      if (!this.alive) return;
      this.clearReconnectTimer();
      const delay = this.reconnectDelay;
      this.reconnectTimer = setTimeout(() => {
        this.reconnectDelay = Math.min(this.reconnectDelay * 2, RECONNECT_MAX_MS);
        this.connect();
      }, delay);
    };

    ws.onerror = () => {
      if (version !== this.connectVersion) return;
      ws.close();
    };
  }
  /* v8 ignore stop */

  private readonly handleVisibilityChange = () => {
    if (typeof document === 'undefined') return;
    if (document.hidden) {
      this.noteHidden();
      return;
    }
    this.recoverFromResume(false);
  };

  private readonly handlePageHide = () => {
    this.noteHidden();
  };

  private readonly handlePageShow = () => {
    this.recoverFromResume(true);
  };

  private readonly handleWindowFocus = () => {
    this.recoverFromResume(false);
  };

  private readonly handleOnline = () => {
    this.recoverFromResume(true);
  };

  private bindBrowserLifecycle() {
    if (typeof window === 'undefined' || typeof document === 'undefined') return;
    document.addEventListener('visibilitychange', this.handleVisibilityChange);
    window.addEventListener('pagehide', this.handlePageHide);
    window.addEventListener('pageshow', this.handlePageShow);
    window.addEventListener('focus', this.handleWindowFocus);
    window.addEventListener('online', this.handleOnline);
  }

  private unbindBrowserLifecycle() {
    if (typeof window === 'undefined' || typeof document === 'undefined') return;
    document.removeEventListener('visibilitychange', this.handleVisibilityChange);
    window.removeEventListener('pagehide', this.handlePageHide);
    window.removeEventListener('pageshow', this.handlePageShow);
    window.removeEventListener('focus', this.handleWindowFocus);
    window.removeEventListener('online', this.handleOnline);
  }

  private recoverFromResume(forceRestartOpen: boolean) {
    if (!this.alive) return;
    if (typeof document !== 'undefined' && document.hidden) return;

    const now = Date.now();
    const resumeEpoch = this.hiddenEpoch;
    const hiddenDuration = this.hiddenAt === null ? 0 : now - this.hiddenAt;
    const shouldRestartOpenSocket = forceRestartOpen || hiddenDuration >= RESUME_RECONNECT_THRESHOLD_MS;
    this.hiddenAt = null;

    if (resumeEpoch > 0 && this.recoveredHiddenEpoch === resumeEpoch) {
      return;
    }

    if (now - this.lastResumeRecoveryAt < RESUME_RECOVERY_DEBOUNCE_MS) {
      return;
    }

    if (!this.ws || this.ws.readyState === WebSocket.CLOSED) {
      this.recoveredHiddenEpoch = resumeEpoch;
      this.lastResumeRecoveryAt = now;
      this.connect();
      return;
    }

    if (this.ws.readyState !== WebSocket.OPEN) {
      this.recoveredHiddenEpoch = resumeEpoch;
      this.lastResumeRecoveryAt = now;
      this.connect();
      return;
    }

    if (!shouldRestartOpenSocket) {
      return;
    }

    this.recoveredHiddenEpoch = resumeEpoch;
    this.lastResumeRecoveryAt = now;
    this.restartConnection();
  }

  private noteHidden() {
    if (this.hiddenAt !== null) return;
    this.hiddenAt = Date.now();
    this.hiddenEpoch++;
  }

  private restartConnection() {
    if (!this.alive) return;

    const ws = this.ws;
    if (!ws) {
      this.connect();
      return;
    }

    if (ws.readyState !== WebSocket.OPEN) {
      this.connect();
      return;
    }

    this.clearReconnectTimer();
    this.connectVersion++;
    this.ws = null;
    this.setConnected(false);
    ws.close();
    this.connect();
  }

  private setConnected(connected: boolean) {
    if (this.connectedState === connected) return;
    this.connectedState = connected;
    for (const handler of this.connectionHandlers) {
      handler(connected);
    }
  }

  private notifyOpen() {
    for (const handler of this.openHandlers) {
      try {
        handler();
      } catch {
        // Ignore subscriber errors so they don't break the shared socket.
      }
    }
  }

  private clearReconnectTimer() {
    if (this.reconnectTimer !== undefined) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = undefined;
    }
  }

  destroy() {
    this.alive = false;
    this.clearReconnectTimer();
    this.unbindBrowserLifecycle();
    this.hiddenAt = null;
    this.setConnected(false);
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
    this.connectionHandlers.clear();
    this.openHandlers.clear();
  }
}

/**
 * Shared WebSocket hook. All consumers share a single connection.
 * Returns `connected` state and a `subscribe` function to register message handlers.
 */
/* v8 ignore start -- WebSocket lifecycle cannot be exercised in jsdom */
export function useAppWebSocket(): AppWebSocketState {
  const [connected, setConnected] = useState(false);

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

    const unsubscribeConnection = instance.subscribeConnection(setConnected);

    return () => {
      unsubscribeConnection();
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

  const subscribeOpen = useCallback((handler: OpenHandler) => {
    if (!sharedInstance) return () => {};
    return sharedInstance.subscribeOpen(handler);
  }, []);

  return { connected, subscribe, send, subscribeOpen };
}
/* v8 ignore stop */
