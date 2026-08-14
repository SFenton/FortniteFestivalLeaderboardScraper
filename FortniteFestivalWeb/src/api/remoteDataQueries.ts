import { queryOptions } from '@tanstack/react-query';
import { api } from './client';
import { queryKeys } from './queryKeys';
import { remoteDataQueryPolicy } from './queryPolicy';

export function playerHistoryQueryOptions(accountId: string, songId: string) {
  return queryOptions({
    queryKey: queryKeys.playerHistory(accountId, songId),
    queryFn: ({ signal }) => (
      api.getPlayerHistory(accountId, songId, undefined, { signal })
        .then(response => response.history)
    ),
    ...remoteDataQueryPolicy,
  });
}

export function rivalsAllQueryOptions(accountId: string) {
  return queryOptions({
    queryKey: queryKeys.rivalsAll(accountId),
    queryFn: ({ signal }) => api.getRivalsAll(accountId, { signal }),
    ...remoteDataQueryPolicy,
  });
}
