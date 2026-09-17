import '@testing-library/jest-dom/vitest';
import '../src/i18n';

// Unit tests must never open a real /api/ws connection. Tests that exercise
// WebSocket behavior replace this inert implementation with their own mock.
class InertTestWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSING = 2;
  static readonly CLOSED = 3;

  readonly CONNECTING = InertTestWebSocket.CONNECTING;
  readonly OPEN = InertTestWebSocket.OPEN;
  readonly CLOSING = InertTestWebSocket.CLOSING;
  readonly CLOSED = InertTestWebSocket.CLOSED;
  readonly url: string;
  readyState = InertTestWebSocket.CONNECTING;
  onopen: ((event: Event) => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onclose: ((event: CloseEvent) => void) | null = null;
  onerror: ((event: Event) => void) | null = null;

  constructor(url: string | URL) {
    this.url = url.toString();
  }

  send() {}

  close() {
    this.readyState = InertTestWebSocket.CLOSED;
  }
}

globalThis.WebSocket =
  InertTestWebSocket as unknown as typeof WebSocket;

// Provide a minimal ResizeObserver stub for jsdom (used by FirstRunCarousel, etc.)
if (typeof globalThis.ResizeObserver === 'undefined') {
  globalThis.ResizeObserver = class ResizeObserver {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof globalThis.ResizeObserver;
}

// Provide a minimal IntersectionObserver stub for jsdom (used by useScrollFade)
if (typeof globalThis.IntersectionObserver === 'undefined') {
  globalThis.IntersectionObserver = class IntersectionObserver {
    constructor(_cb: IntersectionObserverCallback, _opts?: IntersectionObserverInit) {}
    observe() {}
    unobserve() {}
    disconnect() {}
    readonly root = null;
    readonly rootMargin = '';
    readonly thresholds = [] as readonly number[];
    takeRecords(): IntersectionObserverEntry[] { return []; }
  } as unknown as typeof globalThis.IntersectionObserver;
}

// Provide a minimal matchMedia stub for modules that call it at import time
// (e.g. @festival/ui-utils/platform).  Individual tests can override this
// with their own vi.fn() mock in beforeEach.
if (typeof window.matchMedia !== 'function') {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: (query: string) => ({
      matches: false,
      media: query,
      onchange: null,
      addEventListener: () => {},
      removeEventListener: () => {},
      addListener: () => {},
      removeListener: () => {},
      dispatchEvent: () => false,
    }),
  });
}

// jsdom implements document ranges but not range geometry; MarqueeText uses it
// to decide whether text should scroll.
if (typeof Range !== 'undefined' && typeof Range.prototype.getBoundingClientRect !== 'function') {
  Range.prototype.getBoundingClientRect = () => ({
    x: 0,
    y: 0,
    width: 0,
    height: 0,
    top: 0,
    right: 0,
    bottom: 0,
    left: 0,
    toJSON: () => ({}),
  });
}
