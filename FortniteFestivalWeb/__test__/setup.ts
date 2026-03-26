import '@testing-library/jest-dom/vitest';
import '../src/i18n';

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
