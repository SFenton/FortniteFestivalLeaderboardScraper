import { useEffect } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { queryKeys } from '../../api/queryKeys';
import { useAppWebSocket } from './useAppWebSocket';

/**
 * Refreshes the incoming-song list as soon as the service reports a catalog
 * change, so newly ingested songs appear without waiting for the poll.
 */
export function useIncomingSongsRefresh() {
  const queryClient = useQueryClient();
  const { subscribe } = useAppWebSocket();

  useEffect(() => subscribe(message => {
    if (message.type !== 'songs_changed') return;
    void queryClient.invalidateQueries({
      queryKey: queryKeys.incomingSongs(),
    });
  }), [queryClient, subscribe]);
}
