/**
 * Tests for SharedWebSocket lifecycle hardening:
 * - CONNECTING-safe destroy (no close-before-established)
 * - Reconnect timer deduplication
 * - Deferred destroy on refCount bounce (StrictMode / route churn)
 * - Connect-in-progress guard
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';

// ── Mock WebSocket ──

let wsInstances: MockWebSocket[] = [];

class MockWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;

  url: string;
  readyState = MockWebSocket.CONNECTING;
  onopen: ((ev: Event) => void) | null = null;
  onclose: (() => void) | null = null;
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onerror: (() => void) | null = null;
  close = vi.fn(() => {
    if (this.readyState === MockWebSocket.CONNECTING || this.readyState === MockWebSocket.OPEN) {
      this.readyState = MockWebSocket.CLOSING;
    }
    // Schedule onclose asynchronously like a real browser
    setTimeout(() => {
      this.readyState = MockWebSocket.CLOSED;
      this.onclose?.();
    }, 0);
  });
  send = vi.fn();

  constructor(url: string) {
    this.url = url;
    wsInstances.push(this);
  }

  simulateOpen() {
    this.readyState = MockWebSocket.OPEN;
    this.onopen?.({} as Event);
  }

  simulateClose() {
    this.readyState = MockWebSocket.CLOSED;
    this.onclose?.();
  }

  simulateError() {
    this.onerror?.();
  }
}

// We need to import useAppWebSocket AFTER setting up the WebSocket mock,
// because the module creates SharedWebSocket instances that call `new WebSocket()`.
// Use dynamic import + resetModules to get a fresh module for each test.
let useAppWebSocket: typeof import('../../../src/hooks/data/useAppWebSocket').useAppWebSocket;

describe('useAppWebSocket lifecycle', () => {
  let origWebSocket: typeof WebSocket;

  beforeEach(async () => {
    vi.useFakeTimers();
    wsInstances = [];
    origWebSocket = globalThis.WebSocket;
    (globalThis as any).WebSocket = MockWebSocket;

    // Reset module state so each test gets fresh sharedInstance/refCount
    vi.resetModules();
    const mod = await import('../../../src/hooks/data/useAppWebSocket');
    useAppWebSocket = mod.useAppWebSocket;
  });

  afterEach(() => {
    vi.useRealTimers();
    globalThis.WebSocket = origWebSocket;
  });

  it('creates exactly one WebSocket on first consumer mount', () => {
    renderHook(() => useAppWebSocket());
    expect(wsInstances).toHaveLength(1);
    expect(wsInstances[0].url).toContain('/api/ws');
  });

  it('shares the same WebSocket across multiple consumers', () => {
    renderHook(() => useAppWebSocket());
    renderHook(() => useAppWebSocket());
    expect(wsInstances).toHaveLength(1);
  });

  it('reports connected=true after socket opens', () => {
    const { result } = renderHook(() => useAppWebSocket());
    expect(result.current.connected).toBe(false);

    act(() => { wsInstances[0].simulateOpen(); });
    // connected state is polled every 1s
    act(() => { vi.advanceTimersByTime(1_100); });
    expect(result.current.connected).toBe(true);
  });

  // ── CONNECTING-safe destroy ──

  it('does not call ws.close() when socket is still CONNECTING during destroy', () => {
    const { unmount } = renderHook(() => useAppWebSocket());
    const ws = wsInstances[0];
    expect(ws.readyState).toBe(MockWebSocket.CONNECTING);

    unmount();
    // Advance past the deferred destroy window
    act(() => { vi.advanceTimersByTime(500); });

    // close() should NOT have been called while CONNECTING
    expect(ws.close).not.toHaveBeenCalled();
  });

  it('closes socket normally when it is OPEN during destroy', () => {
    const { unmount } = renderHook(() => useAppWebSocket());
    const ws = wsInstances[0];
    act(() => { ws.simulateOpen(); });
    expect(ws.readyState).toBe(MockWebSocket.OPEN);

    unmount();
    act(() => { vi.advanceTimersByTime(500); });

    expect(ws.close).toHaveBeenCalled();
  });

  it('stale onopen after destroy closes the socket and does not reconnect', () => {
    const { unmount } = renderHook(() => useAppWebSocket());
    const ws = wsInstances[0];

    unmount();
    act(() => { vi.advanceTimersByTime(500); });

    // Now simulate the handshake completing (stale callback)
    act(() => { ws.simulateOpen(); });

    // The stale-version guard in onopen should close it
    expect(ws.close).toHaveBeenCalled();
    // No new WebSocket should have been created for reconnect
    expect(wsInstances).toHaveLength(1);
  });

  // ── Reconnect timer deduplication ──

  it('only has one reconnect timer after repeated close events', () => {
    renderHook(() => useAppWebSocket());
    const ws = wsInstances[0];
    act(() => { ws.simulateOpen(); });

    // First close triggers reconnect timer
    act(() => { ws.simulateClose(); });
    const countAfterFirstClose = wsInstances.length;

    // Before reconnect fires, trigger another close (from the same socket)
    // This shouldn't stack another timer because onclose checks connectVersion
    act(() => { ws.simulateClose(); });

    // Advance past reconnect delay — should only create one new socket
    act(() => { vi.advanceTimersByTime(2_000); });
    // At most one new socket from the first reconnect
    expect(wsInstances.length).toBeLessThanOrEqual(countAfterFirstClose + 1);
  });

  // ── Connect-in-progress guard ──

  it('does not create a second socket while one is CONNECTING', () => {
    renderHook(() => useAppWebSocket());
    expect(wsInstances).toHaveLength(1);
    expect(wsInstances[0].readyState).toBe(MockWebSocket.CONNECTING);

    // Mount another consumer — should not create a new socket
    renderHook(() => useAppWebSocket());
    expect(wsInstances).toHaveLength(1);
  });

  // ── Deferred destroy / refCount bounce ──

  it('cancels destroy when a new consumer mounts during the grace window', () => {
    const hook1 = renderHook(() => useAppWebSocket());
    const ws = wsInstances[0];
    act(() => { ws.simulateOpen(); });

    // Unmount the only consumer
    hook1.unmount();
    // Within the deferred window, mount a new consumer
    act(() => { vi.advanceTimersByTime(100); }); // < 300ms
    renderHook(() => useAppWebSocket());

    // Advance past the full window
    act(() => { vi.advanceTimersByTime(500); });

    // Socket should NOT be closed — the deferred destroy was canceled
    expect(ws.close).not.toHaveBeenCalled();
    // Still only one WebSocket instance
    expect(wsInstances).toHaveLength(1);
  });

  it('destroys after grace window when no consumer remounts', () => {
    const { unmount } = renderHook(() => useAppWebSocket());
    const ws = wsInstances[0];
    act(() => { ws.simulateOpen(); });

    unmount();
    act(() => { vi.advanceTimersByTime(500); });

    expect(ws.close).toHaveBeenCalled();
  });

  // ── Reconnect after server-side close ──

  it('reconnects with exponential backoff after repeated failures', () => {
    renderHook(() => useAppWebSocket());
    const ws1 = wsInstances[0];
    act(() => { ws1.simulateOpen(); });

    // Server closes the connection
    act(() => { ws1.simulateClose(); });
    expect(wsInstances).toHaveLength(1);

    // Advance past first reconnect delay (1s base)
    act(() => { vi.advanceTimersByTime(1_100); });
    expect(wsInstances).toHaveLength(2);

    // ws2 fails without opening — delay should double to 2s
    const ws2 = wsInstances[1];
    act(() => { ws2.simulateClose(); });

    // Only 1s elapsed — should NOT have reconnected yet
    act(() => { vi.advanceTimersByTime(1_500); });
    expect(wsInstances).toHaveLength(2);

    // After full 2s+ — should reconnect
    act(() => { vi.advanceTimersByTime(1_000); });
    expect(wsInstances).toHaveLength(3);
  });

  it('resets backoff delay on successful open', () => {
    renderHook(() => useAppWebSocket());
    const ws1 = wsInstances[0];
    act(() => { ws1.simulateOpen(); });
    act(() => { ws1.simulateClose(); });

    // Reconnect at 1s
    act(() => { vi.advanceTimersByTime(1_100); });
    const ws2 = wsInstances[1];
    act(() => { ws2.simulateOpen(); }); // resets delay to 1s
    act(() => { ws2.simulateClose(); });

    // Should reconnect again at 1s (not 2s) because open reset the delay
    act(() => { vi.advanceTimersByTime(1_100); });
    expect(wsInstances).toHaveLength(3);
  });

  // ── Error handling ──

  it('calls close on error (which triggers reconnect via onclose)', () => {
    renderHook(() => useAppWebSocket());
    const ws = wsInstances[0];
    act(() => { ws.simulateOpen(); });

    act(() => { ws.simulateError(); });
    expect(ws.close).toHaveBeenCalled();
  });
});
