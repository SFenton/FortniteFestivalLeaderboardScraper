/**
 * Shared test helpers for rendering pages that need the full provider stack.
 * The API mock must be declared in the actual test file (vi.mock hoists to file scope).
 */
import { type ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SettingsProvider } from '../../src/contexts/SettingsContext';
import { FestivalProvider } from '../../src/contexts/FestivalContext';
import { ShopProvider } from '../../src/contexts/ShopContext';
import { FabSearchProvider } from '../../src/contexts/FabSearchContext';
import { SearchQueryProvider } from '../../src/contexts/SearchQueryContext';
import { PlayerDataProvider } from '../../src/contexts/PlayerDataContext';
import { FirstRunProvider } from '../../src/contexts/FirstRunContext';
import { ScrollContainerProvider, useScrollContainer, useHeaderPortalRef } from '../../src/contexts/ScrollContainerContext';

/** Create a QueryClient configured for tests: no retries, no gc delays. */
export function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

/**
 * Injects mock scroll container and portal target elements into context
 * so Page's portal rendering and scroll hooks work in tests.
 * Renders the portal target inline so portaled content appears in the render container.
 */
function ShellRefInjector({ children }: { children: ReactNode }) {
  const scrollRef = useScrollContainer();
  const setPortalNode = useHeaderPortalRef();

  return (
    <>
      <div ref={setPortalNode} data-testid="test-header-portal" />
      <div ref={(el) => {
        if (el && !scrollRef.current) {
          Object.defineProperty(el, 'scrollHeight', { value: 5000, writable: true, configurable: true });
          Object.defineProperty(el, 'scrollTop', { value: 0, writable: true, configurable: true });
          Object.defineProperty(el, 'clientHeight', { value: 800, writable: true, configurable: true });
          el.scrollTo = (() => {}) as any;
          scrollRef.current = el;
        }
      }} data-testid="test-scroll-container">
        {children}
      </div>
    </>
  );
}

export function TestProviders({ children, route = '/', accountId }: { children: ReactNode; route?: string; accountId?: string }) {
  const qc = createTestQueryClient();
  return (
    <QueryClientProvider client={qc}>
    <SettingsProvider>
      <FestivalProvider>
        <ShopProvider>
        <FabSearchProvider>
          <SearchQueryProvider>
            <PlayerDataProvider accountId={accountId}>
              <FirstRunProvider>
                <ScrollContainerProvider>
                <ShellRefInjector>
                <MemoryRouter initialEntries={[route]}>
                  {children}
                </MemoryRouter>
                </ShellRefInjector>
                </ScrollContainerProvider>
              </FirstRunProvider>
            </PlayerDataProvider>
          </SearchQueryProvider>
        </FabSearchProvider>
        </ShopProvider>
      </FestivalProvider>
    </SettingsProvider>
    </QueryClientProvider>
  );
}
