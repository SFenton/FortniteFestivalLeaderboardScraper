import { act, renderHook } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { WsNotificationMessage } from '@festival/core/api';
import { queryKeys } from '../../../src/api/queryKeys';
import { useCatalogPublicationLag } from '../../../src/hooks/data/useCatalogPublicationLag';

const mocks = vi.hoisted(() => ({
  handler: null as ((message: WsNotificationMessage) => void) | null,
  unsubscribe: vi.fn(),
  serviceInfo: {
    data: {
      catalog: {
        syncIntervalSeconds: 300,
        live: { version: 12, songCount: 710 },
        published: {
          publicationId: 114,
          version: 11,
          songCount: 707,
        },
        working: {
          publicationId: 116,
          version: 12,
          songCount: 710,
        },
        awaitingPublication: 3,
        addedAwaitingPublication: 3,
        changedAwaitingPublication: 0,
        removedAwaitingPublication: 0,
        pathGenerationPending: 0,
        pathGenerationReviewRequired: 0,
      },
    },
  },
}));

vi.mock('../../../src/hooks/data/useServiceInfo', () => ({
  useServiceInfo: () => mocks.serviceInfo,
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

describe('useCatalogPublicationLag', () => {
  beforeEach(() => {
    mocks.handler = null;
    mocks.unsubscribe.mockClear();
  });

  it('returns catalog lag and refreshes it after catalog changes', async () => {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: { retry: false },
      },
    });
    const invalidate = vi.spyOn(queryClient, 'invalidateQueries')
      .mockResolvedValue();
    const { result, unmount } = renderHook(
      () => useCatalogPublicationLag(),
      { wrapper: makeWrapper(queryClient) },
    );

    expect(result.current?.awaitingPublication).toBe(3);
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
    expect(invalidate).toHaveBeenCalledWith({
      queryKey: queryKeys.serviceInfo(),
    });

    unmount();
    expect(mocks.unsubscribe).toHaveBeenCalledOnce();
  });
});
