/**
 * Shared test helpers for rendering pages that need the full provider stack.
 * The API mock must be declared in the actual test file (vi.mock hoists to file scope).
 */
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SettingsProvider } from '../../contexts/SettingsContext';
import { FestivalProvider } from '../../contexts/FestivalContext';
import { FabSearchProvider } from '../../contexts/FabSearchContext';
import { SearchQueryProvider } from '../../contexts/SearchQueryContext';
import { PlayerDataProvider } from '../../contexts/PlayerDataContext';

/** Create a QueryClient configured for tests: no retries, no gc delays. */
export function createTestQueryClient() {
  return new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
      mutations: { retry: false },
    },
  });
}

export function TestProviders({ children, route = '/', accountId }: { children: ReactNode; route?: string; accountId?: string }) {
  const qc = createTestQueryClient();
  return (
    <QueryClientProvider client={qc}>
    <SettingsProvider>
      <FestivalProvider>
        <FabSearchProvider>
          <SearchQueryProvider>
            <PlayerDataProvider accountId={accountId}>
              <MemoryRouter initialEntries={[route]}>
                {children}
              </MemoryRouter>
            </PlayerDataProvider>
          </SearchQueryProvider>
        </FabSearchProvider>
      </FestivalProvider>
    </SettingsProvider>
    </QueryClientProvider>
  );
}
