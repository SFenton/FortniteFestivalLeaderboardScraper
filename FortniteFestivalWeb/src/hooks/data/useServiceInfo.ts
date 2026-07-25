import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';

export const SERVICE_INFO_TIMEOUT_MS = 3_000;
export const SERVICE_INFO_STALE_TIME_MS = 5_000;
export const SERVICE_INFO_SETTINGS_POLL_MS = 5_000;
export const SERVICE_INFO_AVAILABILITY_POLL_MS = 30_000;
export const SERVICE_INFO_UNAVAILABLE_RETRY_MS = 5_000;
export const SERVICE_INFO_GC_TIME_MS = 10 * 60_000;

export type ServiceInfoConsumer = 'availability' | 'settings';

async function fetchServiceInfo() {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), SERVICE_INFO_TIMEOUT_MS);

  try {
    return await api.getServiceInfo(controller.signal);
  } finally {
    clearTimeout(timeout);
  }
}

export function useServiceInfo(consumer: ServiceInfoConsumer) {
  const successPollInterval = consumer === 'settings'
    ? SERVICE_INFO_SETTINGS_POLL_MS
    : SERVICE_INFO_AVAILABILITY_POLL_MS;

  return useQuery({
    queryKey: queryKeys.serviceInfo(),
    queryFn: fetchServiceInfo,
    staleTime: SERVICE_INFO_STALE_TIME_MS,
    gcTime: SERVICE_INFO_GC_TIME_MS,
    retry: false,
    networkMode: 'always',
    refetchOnMount: true,
    refetchOnWindowFocus: false,
    refetchOnReconnect: false,
    refetchIntervalInBackground: true,
    refetchInterval: query => query.state.status === 'error'
      ? SERVICE_INFO_UNAVAILABLE_RETRY_MS
      : successPollInterval,
  });
}
