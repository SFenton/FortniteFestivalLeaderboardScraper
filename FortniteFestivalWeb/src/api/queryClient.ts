import { QueryClient } from '@tanstack/react-query';

export const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 5 * 60 * 1000,       // 5 min — matches previous TTL cache
      gcTime: 10 * 60 * 1000,          // 10 min garbage collection
      retry: 1,
      refetchOnWindowFocus: false,
    },
  },
});
