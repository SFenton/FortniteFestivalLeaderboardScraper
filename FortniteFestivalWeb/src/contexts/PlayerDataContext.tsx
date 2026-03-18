import {
  createContext,
  useContext,
  useCallback,
  useEffect,
  useMemo,
  type ReactNode,
} from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import { queryKeys } from '../api/queryKeys';
import { useSyncStatus, type SyncPhase } from '../hooks/data/useSyncStatus';
import type { PlayerResponse } from '@festival/core/api/serverTypes';

type PlayerDataContextValue = {
  playerData: PlayerResponse | null;
  playerLoading: boolean;
  playerError: string | null;
  refreshPlayer: () => Promise<void>;
  isSyncing: boolean;
  syncPhase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
};

const PlayerDataContext = createContext<PlayerDataContextValue | null>(null);

export function PlayerDataProvider({
  accountId,
  children,
}: {
  accountId: string | undefined;
  children: ReactNode;
}) {
  const qc = useQueryClient();

  const { data, isLoading, error } = useQuery({
    queryKey: queryKeys.player(accountId ?? ''),
    queryFn: () => api.getPlayer(accountId!),
    enabled: !!accountId,
  });

  const { isSyncing, phase, backfillProgress, historyProgress, justCompleted, clearCompleted } =
    useSyncStatus(accountId);

  // Auto-reload when sync completes
  /* v8 ignore start — sync-complete invalidation */
  useEffect(() => {
    if (justCompleted && accountId) {
      clearCompleted();
      void qc.invalidateQueries({ queryKey: queryKeys.player(accountId) });
    }
  }, [justCompleted, accountId, clearCompleted, qc]);
  /* v8 ignore stop */

  const refreshPlayer = useCallback(async () => {
    if (accountId) {
      await qc.invalidateQueries({ queryKey: queryKeys.player(accountId) });
    }
  }, [accountId, qc]);

  const value = useMemo<PlayerDataContextValue>(() => ({
    playerData: data ?? null,
    playerLoading: isLoading,
    playerError: error ? (error instanceof Error ? error.message : 'Failed to load player') : null,
    refreshPlayer,
    isSyncing,
    syncPhase: phase,
    backfillProgress,
    historyProgress,
  }), [data, isLoading, error, refreshPlayer, isSyncing, phase, backfillProgress, historyProgress]);

  return (
    <PlayerDataContext.Provider value={value}>
      {children}
    </PlayerDataContext.Provider>
  );
}

export function usePlayerData(): PlayerDataContextValue {
  const ctx = useContext(PlayerDataContext);
  if (!ctx) {
    throw new Error('usePlayerData must be used within a PlayerDataProvider');
  }
  return ctx;
}
