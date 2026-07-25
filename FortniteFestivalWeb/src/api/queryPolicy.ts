import { keepPreviousData, type QueryClient } from '@tanstack/react-query';
import type { AllLeaderboardsResponse, LeaderboardResponse } from '@festival/core/api/serverTypes';
import { queryKeys } from './queryKeys';

export const REMOTE_DATA_STALE_TIME_MS = 5 * 60_000;
export const REMOTE_DATA_GC_TIME_MS = 10 * 60_000;

export const remoteDataQueryPolicy = {
  staleTime: REMOTE_DATA_STALE_TIME_MS,
  gcTime: REMOTE_DATA_GC_TIME_MS,
  retry: false,
  retryOnMount: false,
  refetchOnWindowFocus: false,
} as const;

export function keepPreviousLeaderboardPage(
  songId: string,
  instrument: string,
) {
  return (previousData: LeaderboardResponse | undefined) => (
    previousData?.songId === songId && previousData.instrument === instrument
      ? keepPreviousData(previousData)
      : undefined
  );
}

export function keepPreviousSongLeaderboards(songId: string) {
  return (previousData: AllLeaderboardsResponse | undefined) => (
    previousData?.songId === songId
      ? keepPreviousData(previousData)
      : undefined
  );
}

export async function invalidateLeaderboardData(queryClient: QueryClient): Promise<void> {
  await Promise.all([
    queryClient.invalidateQueries({ queryKey: queryKeys.leaderboardRoot() }),
    queryClient.invalidateQueries({ queryKey: queryKeys.allLeaderboardsRoot() }),
  ]);
}
