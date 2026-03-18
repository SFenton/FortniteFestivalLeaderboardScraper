import '@testing-library/jest-dom/vitest';
import '../src/i18n';

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
