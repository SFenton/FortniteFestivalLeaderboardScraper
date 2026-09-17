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
  }), [queryClient, subscribe]);

  return serviceInfo.data?.catalog ?? null;
}
