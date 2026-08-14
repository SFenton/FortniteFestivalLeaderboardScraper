import { useQuery } from '@tanstack/react-query';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';

const MINUTE_MS = 60_000;

export const SERVICE_INFO_TIMEOUT_MS = 3_000;
export const SERVICE_INFO_STALE_TIME_MS = 5_000;
export const SERVICE_INFO_SETTINGS_POLL_MS = 5_000;
export const SERVICE_INFO_AVAILABILITY_POLL_MS = 30_000;
export const SERVICE_INFO_BACKGROUND_POLL_MS = 30_000;
export const SERVICE_INFO_UNAVAILABLE_RETRY_MS = 5_000;
export const SERVICE_INFO_GC_TIME_MS = 10 * MINUTE_MS;

export type ServiceInfoConsumer = 'availability' | 'settings';

export class ServiceInfoTimeoutError extends Error {
  override name = 'TimeoutError';
}

async function fetchServiceInfo({ signal }: { signal: AbortSignal }) {
  const controller = new AbortController();
  let timedOut = false;
  const abortFromCaller = () => controller.abort(signal.reason);
  if (signal.aborted) abortFromCaller();
  else signal.addEventListener('abort', abortFromCaller, { once: true });
  const timeout = setTimeout(() => {
    timedOut = true;
    controller.abort(new DOMException('Service info request timed out', 'TimeoutError'));
  }, SERVICE_INFO_TIMEOUT_MS);

  try {
    return await api.getServiceInfo(controller.signal);
  } catch (error) {
    if (timedOut && !signal.aborted) {
      throw new ServiceInfoTimeoutError(`Service info request timed out after ${SERVICE_INFO_TIMEOUT_MS} ms`);
    }
    throw error;
  } finally {
    clearTimeout(timeout);
    signal.removeEventListener('abort', abortFromCaller);
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
    refetchInterval: query => {
      if (query.state.status === 'error') return SERVICE_INFO_UNAVAILABLE_RETRY_MS;
      if (consumer === 'settings' && document.visibilityState === 'hidden') {
        return SERVICE_INFO_BACKGROUND_POLL_MS;
      }
      return successPollInterval;
    },
  });
}
