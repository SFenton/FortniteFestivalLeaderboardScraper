import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { renderHook, act } from '@testing-library/react';
import { useShopWebSocket } from '../../../src/hooks/data/useShopWebSocket';
import type { WsNotificationMessage } from '@festival/core/api/serverTypes';

const mockAppWebSocket = vi.hoisted(() => ({
  connected: true,
  handler: null as ((msg: WsNotificationMessage) => void) | null,
  unsubscribe: vi.fn(),
}));

vi.mock('../../../src/hooks/data/useAppWebSocket', () => ({
  useAppWebSocket: () => ({
    connected: mockAppWebSocket.connected,
    subscribe: (handler: (msg: WsNotificationMessage) => void) => {
      mockAppWebSocket.handler = handler;
      return mockAppWebSocket.unsubscribe;
    },
    send: vi.fn(),
    subscribeOpen: vi.fn(),
  }),
}));

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
    mockAppWebSocket.connected = true;
    mockAppWebSocket.handler = null;
    mockAppWebSocket.unsubscribe.mockClear();
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

  it('seeds from initialNewIds', () => {
    const initialNew = new Set(['s2']);
    const { result } = renderHook(() => useShopWebSocket(new Set(['s1', 's2']), null, initialNew));
    expect(result.current.newShopIds).toEqual(initialNew);
  });

  it('handles shop_snapshot message with newSongs', () => {
    const { result } = renderHook(() => useShopWebSocket(null));

    act(() => {
      mockAppWebSocket.handler?.({
        type: 'shop_snapshot',
        songs: [
          { songId: 's1', title: 'Song One', artist: 'Artist', shopUrl: '/s1' },
          { songId: 's2', title: 'Song Two', artist: 'Artist', shopUrl: '/s2', isNew: true },
        ],
        total: 2,
        leavingTomorrow: [],
        newSongs: ['s2'],
      });
    });

    expect(result.current.connected).toBe(true);
    expect(result.current.shopSongIds?.has('s1')).toBe(true);
    expect(result.current.newShopIds?.has('s2')).toBe(true);
    expect(result.current.shopSongsMap?.get('s2')?.isNew).toBe(true);
  });

  it('handles shop_changed newSongs updates', () => {
    const { result } = renderHook(() => useShopWebSocket(new Set(['s1'])));

    act(() => {
      mockAppWebSocket.handler?.({
        type: 'shop_changed',
        added: [{ songId: 's2', title: 'Song Two', artist: 'Artist', shopUrl: '/s2' }],
        removed: [],
        total: 2,
        leavingTomorrow: [],
        newSongs: ['s2'],
      });
    });

    expect(result.current.shopSongIds?.has('s2')).toBe(true);
    expect(result.current.newShopIds?.has('s2')).toBe(true);
    expect(result.current.shopSongsMap?.get('s2')?.isNew).toBe(true);
  });

  it('cleans up on unmount', () => {
    const { unmount } = renderHook(() => useShopWebSocket(null));
    act(() => { vi.advanceTimersByTime(10); });
    unmount();
    // No error means cleanup worked
  });
});
