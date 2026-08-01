import { QueryClient } from '@tanstack/react-query';
import { isServiceUnavailableError } from '../utils/apiError';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5 * 60 * 1000,       // 5 min — matches previous TTL cache
      gcTime: 10 * 60 * 1000,          // 10 min garbage collection
      retry: (failureCount, error) =>
        failureCount < 1 && !isServiceUnavailableError(error),
      refetchOnWindowFocus: false,
    },
  },
});
