import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '../../api/queryKeys';
import { useAppWebSocket } from './useAppWebSocket';
import { useServiceInfo } from './useServiceInfo';

export function useCatalogPublicationLag() {
  const serviceInfo = useServiceInfo('availability');
  const queryClient = useQueryClient();
  const { subscribe } = useAppWebSocket();

  useEffect(() => subscribe(message => {
    if (message.type !== 'songs_changed') return;
    void queryClient.invalidateQueries({
      queryKey: queryKeys.serviceInfo(),
    });
    void queryClient.invalidateQueries({
      queryKey: queryKeys.incomingSongs(),
    });
  }), [queryClient, subscribe]);

  return serviceInfo.data?.catalog ?? null;
}

type CatalogLagCounts = {
  awaitingPublication: number | null;
  addedAwaitingPublication?: number | null;
  changedAwaitingPublication?: number | null;
  removedAwaitingPublication?: number | null;
};

/**
 * Catalog changes that still wait for a publication. Added songs are excluded:
 * they are listed immediately from /api/songs/incoming, so only metadata
 * changes and removals remain publication-gated.
 */
export function countCatalogChangesAwaitingPublication(
  lag: CatalogLagCounts | null | undefined,
): number {
  if (!lag) return 0;
  const changed = lag.changedAwaitingPublication;
  const removed = lag.removedAwaitingPublication;
  if (typeof changed === 'number' || typeof removed === 'number') {
    return Math.max(0, (changed ?? 0) + (removed ?? 0));
  }
  const total = lag.awaitingPublication;
  if (typeof total !== 'number') return 0;
  return Math.max(0, total - (lag.addedAwaitingPublication ?? 0));
}
