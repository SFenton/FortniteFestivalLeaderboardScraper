/**
 * Browser API stubs for testing in jsdom.
 *
 * Import and call in beforeAll/beforeEach as needed.
 */
import { vi } from 'vitest';

/** Stub Element.prototype.scrollTo (not supported in jsdom). */
export function stubScrollTo() {
  Element.prototype.scrollTo = vi.fn() as unknown as typeof Element.prototype.scrollTo;
  Element.prototype.scrollIntoView = vi.fn();
}

/**
 * Stub clientHeight/scrollHeight so that virtual lists and scroll-dependent
 * logic works in jsdom (where elements have 0 dimensions by default).
 */
export function stubElementDimensions(height = 800) {
  Object.defineProperty(HTMLElement.prototype, 'clientHeight', {
    configurable: true,
    get() { return height; },
  });
  Object.defineProperty(HTMLElement.prototype, 'scrollHeight', {
    configurable: true,
    get() { return height * 2; },
  });
  Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
    configurable: true,
    get() { return 1024; },
  });
  Object.defineProperty(HTMLElement.prototype, 'offsetHeight', {
    configurable: true,
    get() { return height; },
  });
  // getBoundingClientRect for scroll-based measurements
  Element.prototype.getBoundingClientRect = vi.fn().mockReturnValue({
    top: 0,
    left: 0,
    bottom: height,
    right: 1024,
    width: 1024,
    height,
    x: 0,
    y: 0,
    toJSON() { return this; },
  });
}

/** Minimal ResizeObserver mock (needed by @tanstack/react-virtual). */
export function stubResizeObserver(contentRect = { width: 800, height: 600 }) {
  const observers: { cb: ResizeObserverCallback; targets: Element[] }[] = [];

  class MockResizeObserver {
    private _cb: ResizeObserverCallback;
    private _targets: Element[] = [];

    constructor(cb: ResizeObserverCallback) {
      this._cb = cb;
      observers.push({ cb: this._cb, targets: this._targets });
    }

    observe(target: Element) {
      this._targets.push(target);
      // Fire callback synchronously with a mock entry
      this._cb(
        [{ target, contentRect, borderBoxSize: [], contentBoxSize: [], devicePixelContentBoxSize: [] } as unknown as ResizeObserverEntry],
        this as unknown as ResizeObserver,
      );
    }

    unobserve(target: Element) {
      this._targets = this._targets.filter(t => t !== target);
    }

    disconnect() {
      this._targets = [];
    }
  }

  vi.stubGlobal('ResizeObserver', MockResizeObserver);
  return observers;
}

/** Minimal IntersectionObserver mock (needed by InfiniteScroll). */
export function stubIntersectionObserver() {
  class MockIntersectionObserver {
    constructor(_cb: IntersectionObserverCallback, _options?: IntersectionObserverInit) {}
    observe() {}
    unobserve() {}
    disconnect() {}
  }

  vi.stubGlobal('IntersectionObserver', MockIntersectionObserver);
}

/** Configurable matchMedia stub. */
export function stubMatchMedia(matches = false) {
  const mock = vi.fn().mockImplementation((query: string) => ({
    matches,
    media: query,
    onchange: null,
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  }));

  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: mock,
  });

  return mock;
}
