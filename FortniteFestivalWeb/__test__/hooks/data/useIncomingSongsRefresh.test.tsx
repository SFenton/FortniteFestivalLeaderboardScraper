import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WsNotificationMessage } from '@festival/core/api';
import { queryKeys } from '../../../src/api/queryKeys';
import { useIncomingSongsRefresh } from '../../../src/hooks/data/useIncomingSongsRefresh';

const mocks = vi.hoisted(() => ({
  handler: null as ((message: WsNotificationMessage) => void) | null,
  unsubscribe: vi.fn(),
}));

vi.mock('../../../src/hooks/data/useAppWebSocket', () => ({
  useAppWebSocket: () => ({
    connected: true,
    subscribe: (
      handler: (message: WsNotificationMessage) => void,
    ) => {
      mocks.handler = handler;
      return mocks.unsubscribe;
    },
    send: vi.fn(),
    subscribeOpen: vi.fn(),
  }),
}));

function makeWrapper(queryClient: QueryClient) {
  return function Wrapper({ children }: { children: ReactNode }) {
    return (
      <QueryClientProvider client={queryClient}>
        {children}
      </QueryClientProvider>
    );
  };
}

describe('useIncomingSongsRefresh', () => {
  beforeEach(() => {
    mocks.handler = null;
    mocks.unsubscribe.mockClear();
  });

  it('refreshes only incoming songs after catalog changes', async () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
      .mockResolvedValue();
    const { unmount } = renderHook(
      () => useIncomingSongsRefresh(),
      { wrapper: makeWrapper(queryClient) },
    );

    expect(mocks.handler).not.toBeNull();

    act(() => {
      mocks.handler?.({
        type: 'scores_changed',
        at: '2026-08-25T14:15:00Z',
      });
    });
    expect(invalidate).not.toHaveBeenCalled();

    await act(async () => {
      mocks.handler?.({
        type: 'songs_changed',
        total: 710,
        added: 3,
        awaitingPublication: 3,
        at: '2026-08-25T14:15:00Z',
      });
      await Promise.resolve();
    });
    expect(invalidate).toHaveBeenCalledOnce();
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: queryKeys.incomingSongs(),
    });

    unmount();
    expect(mocks.unsubscribe).toHaveBeenCalledOnce();
  });
});

describe('countCatalogChangesAwaitingPublication', () => {
  it('excludes added songs because they are listed immediately', async () => {
    const { countCatalogChangesAwaitingPublication } = await import('../../../src/hooks/data/useCatalogPublicationLag');
    expect(countCatalogChangesAwaitingPublication(null)).toBe(0);
    expect(countCatalogChangesAwaitingPublication({ awaitingPublication: 2, addedAwaitingPublication: 2, changedAwaitingPublication: 0, removedAwaitingPublication: 0 })).toBe(0);
    expect(countCatalogChangesAwaitingPublication({ awaitingPublication: 3, addedAwaitingPublication: 1, changedAwaitingPublication: 1, removedAwaitingPublication: 1 })).toBe(2);
    expect(countCatalogChangesAwaitingPublication({ awaitingPublication: 3, addedAwaitingPublication: 1 })).toBe(2);
    expect(countCatalogChangesAwaitingPublication({ awaitingPublication: null })).toBe(0);
  });
});
