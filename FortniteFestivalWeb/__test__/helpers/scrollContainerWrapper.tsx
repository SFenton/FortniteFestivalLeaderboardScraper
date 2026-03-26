/**
 * Test wrapper that provides a mock scroll container element via ScrollContainerContext.
 * Used by scroll hook tests (useScrollRestore, useHeaderCollapse, useScrollFade, etc.)
 * that read from the context instead of window.
 */
import type { ReactNode } from 'react';
import { ScrollContainerProvider, useScrollContainer } from '../../src/contexts/ScrollContainerContext';

/**
 * Creates a wrapper component + a mock scroll container div.
 * The wrapper sets ScrollContainerContext.current to the mock element.
 * Returns both so tests can manipulate scrollTop/scrollHeight on the element.
 */
export function createScrollContainerWrapper() {
  const mockEl = document.createElement('div');
  // Provide sensible defaults
  Object.defineProperty(mockEl, 'scrollHeight', { value: 5000, writable: true, configurable: true });
  Object.defineProperty(mockEl, 'scrollTop', { value: 0, writable: true, configurable: true });
  Object.defineProperty(mockEl, 'clientHeight', { value: 800, writable: true, configurable: true });
  mockEl.scrollTo = (() => {}) as any;

  function Injector({ children }: { children: ReactNode }) {
    const ref = useScrollContainer();
    ref.current = mockEl as any;
    return <>{children}</>;
  }

  function wrapper({ children }: { children: ReactNode }) {
    return (
      <ScrollContainerProvider>
        <Injector>{children}</Injector>
      </ScrollContainerProvider>
    );
  }

  return { wrapper, mockEl };
}
