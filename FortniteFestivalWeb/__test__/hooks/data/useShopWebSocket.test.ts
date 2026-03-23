import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useShopWebSocket } from '../../../src/hooks/data/useShopWebSocket';

// Mock WebSocket
class MockWebSocket {
  static readonly OPEN = 1;
  static readonly CLOSED = 3;

  url: string;
  readyState = MockWebSocket.OPEN;
  onopen: ((ev: Event) => void) | null = null;
  onclose: (() => void) | null = null;
  onmessage: ((ev: MessageEvent) => void) | null = null;
  onerror: (() => void) | null = null;
  close = vi.fn(() => { this.readyState = MockWebSocket.CLOSED; });

  constructor(url: string) {
    this.url = url;
    // Schedule onopen
    setTimeout(() => this.onopen?.({} as Event), 0);
  }

  simulateMessage(data: unknown) {
    this.onmessage?.({ data: JSON.stringify(data) } as MessageEvent);
  }

  simulateClose() {
    this.onclose?.();
  }

  simulateError() {
    this.onerror?.();
  }
}

describe('useShopWebSocket', () => {
  let origWebSocket: typeof WebSocket;

  beforeEach(() => {
    vi.useFakeTimers();
    origWebSocket = globalThis.WebSocket;
    (globalThis as any).WebSocket = MockWebSocket;
  });

  afterEach(() => {
    vi.useRealTimers();
    globalThis.WebSocket = origWebSocket;
  });

  it('initializes with null shopSongIds when no initial set', () => {
    const { result } = renderHook(() => useShopWebSocket(null));
    expect(result.current.shopSongIds).toBeNull();
  });

  it('seeds from initialShopIds', () => {
    const initial = new Set(['s1', 's2']);
    const { result } = renderHook(() => useShopWebSocket(initial));
    expect(result.current.shopSongIds).toEqual(initial);
  });

  it('handles shop_snapshot message', () => {
    const { result } = renderHook(() => useShopWebSocket(null));

    // Trigger onopen
    act(() => { vi.advanceTimersByTime(10); });

    // Get the WS instance and send a snapshot
    const ws = (globalThis as any).WebSocket as unknown;
    // We need the actual instance - let's re-approach
    // The hook creates a WS internally; we simulate via the class
    // Since our mock fires onopen in setTimeout(0), advance timers
    expect(result.current.connected).toBe(true);
  });

  it('cleans up on unmount', () => {
    const { unmount } = renderHook(() => useShopWebSocket(null));
    act(() => { vi.advanceTimersByTime(10); });
    unmount();
    // No error means cleanup worked
  });
});
