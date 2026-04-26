/* eslint-disable no-magic-numbers -- prefetch timing constants mirror the app's query cache defaults. */
import { useEffect } from 'react';
import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import type { PlayerBandListGroup } from '@festival/core/api/serverTypes';
import { api } from '../../api/client';
import { queryKeys } from '../../api/queryKeys';
import { useFeatureFlags } from '../../contexts/FeatureFlagsContext';

const PREFETCH_PAGE = 1;
const PREFETCH_PAGE_SIZE = 25;
const PREFETCH_STALE_TIME_MS = 5 * 60_000;
const PRIMARY_GROUP: PlayerBandListGroup = 'all';
const IDLE_GROUPS: PlayerBandListGroup[] = ['duos', 'trios', 'quads'];

type IdleCallback = (deadline: { didTimeout: boolean; timeRemaining: () => number }) => void;
type IdleScheduler = typeof globalThis & {
  requestIdleCallback?: (callback: IdleCallback, options?: { timeout?: number }) => number;
  cancelIdleCallback?: (handle: number) => void;
};

function playerBandsAccountKey(accountId: string) {
  return ['playerBandsList', accountId] as const;
}

function scheduleIdle(callback: () => void): () => void {
  const scheduler = globalThis as IdleScheduler;
  if (typeof scheduler.requestIdleCallback === 'function') {
    const handle = scheduler.requestIdleCallback(() => callback(), { timeout: 1_500 });
    return () => scheduler.cancelIdleCallback?.(handle);
  }

  const timeoutId = window.setTimeout(callback, 150);
  return () => window.clearTimeout(timeoutId);
}

function prefetchBandsGroup(queryClient: QueryClient, accountId: string, group: PlayerBandListGroup) {
  void queryClient.prefetchQuery({
    queryKey: queryKeys.playerBandsList(accountId, group, PREFETCH_PAGE, PREFETCH_PAGE_SIZE),
    queryFn: ({ signal }) => api.getPlayerBandsList(accountId, group, PREFETCH_PAGE, PREFETCH_PAGE_SIZE, { signal }),
    staleTime: PREFETCH_STALE_TIME_MS,
  });
}

export function usePlayerBandsPrefetch(accountId: string | undefined) {
  const queryClient = useQueryClient();
  const { playerBands: playerBandsEnabled } = useFeatureFlags();

  useEffect(() => {
    if (!accountId || !playerBandsEnabled) return;

    prefetchBandsGroup(queryClient, accountId, PRIMARY_GROUP);
    const cancelIdlePrefetch = scheduleIdle(() => {
      for (const group of IDLE_GROUPS) {
        prefetchBandsGroup(queryClient, accountId, group);
      }
    });

    return () => {
      cancelIdlePrefetch();
      const queryKey = playerBandsAccountKey(accountId);
      void queryClient.cancelQueries({ queryKey, type: 'inactive' });
      queryClient.removeQueries({ queryKey, type: 'inactive' });
    };
  }, [accountId, playerBandsEnabled, queryClient]);
}