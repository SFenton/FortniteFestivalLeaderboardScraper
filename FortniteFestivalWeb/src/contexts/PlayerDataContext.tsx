import {
  createContext,
  useContext,
  useCallback,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { api } from '../api/client';
import { queryKeys } from '../api/queryKeys';
import { useSyncStatus, type SyncPhase } from '../hooks/data/useSyncStatus';
import type { PlayerResponse } from '@festival/core/api/serverTypes';

const DISMISSED_STORAGE_KEY = 'fst:syncBannerDismissed';

function loadDismissed(accountId: string | undefined): boolean {
  if (!accountId) return false;
  try {
    const raw = localStorage.getItem(DISMISSED_STORAGE_KEY);
    if (!raw) return false;
    const parsed = JSON.parse(raw);
    return parsed?.accountId === accountId && parsed?.dismissed === true;
  } catch { return false; }
}

function saveDismissed(accountId: string | undefined, dismissed: boolean) {
  if (!accountId) return;
  if (dismissed) {
    localStorage.setItem(DISMISSED_STORAGE_KEY, JSON.stringify({ accountId, dismissed: true }));
  } else {
    localStorage.removeItem(DISMISSED_STORAGE_KEY);
  }
}

type PlayerDataContextValue = {
  playerData: PlayerResponse | null;
  playerLoading: boolean;
  playerError: string | null;
  refreshPlayer: () => Promise<void>;
  isSyncing: boolean;
  syncPhase: SyncPhase;
  backfillProgress: number;
  historyProgress: number;
  rivalsProgress: number;
  entriesFound: number;
  itemsCompleted: number;
  totalItems: number;
  currentSongName: string | null;
  seasonsQueried: number;
  rivalsFound: number;
  isThrottled: boolean;
  throttleStatusKey: string | null;
  pendingRankUpdate: boolean;
  estimatedRankUpdateMinutes: number | null;
  probeStatusKey: string | null;
  nextRetrySeconds: number | null;
  justCompleted: boolean;
  clearCompleted: () => void;
  /** True when the sync-complete banner has been dismissed (persisted across refresh). */
  syncBannerDismissed: boolean;
  /** Dismiss the sync-complete banner globally (persists to localStorage). */
  dismissSyncBanner: () => void;
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

  const { isSyncing, phase, backfillProgress, historyProgress, rivalsProgress, entriesFound, itemsCompleted, totalItems, currentSongName, seasonsQueried, rivalsFound, isThrottled, throttleStatusKey, pendingRankUpdate, estimatedRankUpdateMinutes, probeStatusKey, nextRetrySeconds, justCompleted, clearCompleted } =
    useSyncStatus(accountId);

  // Separate flag that consumers can read independently of the one-shot justCompleted
  const [syncCompleted, setSyncCompleted] = useState(false);
  const clearSyncCompleted = useCallback(() => setSyncCompleted(false), []);

  // Persisted dismissal state for sync-complete banner (scoped by account)
  const [syncBannerDismissed, setSyncBannerDismissed] = useState(() => loadDismissed(accountId));
  const dismissSyncBanner = useCallback(() => {
    setSyncBannerDismissed(true);
    saveDismissed(accountId, true);
  }, [accountId]);

  // Auto-reload when sync completes + set consumer-visible flag
  // Also reset dismissal so the new completion banner shows
  /* v8 ignore start — sync-complete invalidation */
  useEffect(() => {
    if (justCompleted && accountId) {
      clearCompleted();
      setSyncCompleted(true);
      setSyncBannerDismissed(false);
      saveDismissed(accountId, false);
      void qc.invalidateQueries({ queryKey: queryKeys.player(accountId) });
    }
  }, [justCompleted, accountId, clearCompleted, qc]);
  /* v8 ignore stop */

  /* v8 ignore start — accountId always present when provider is mounted */
  const refreshPlayer = useCallback(async () => {
    if (accountId) {
      await qc.invalidateQueries({ queryKey: queryKeys.player(accountId) });
    }
  }, [accountId, qc]);
  /* v8 ignore stop */

  const value = useMemo<PlayerDataContextValue>(() => ({
    playerData: data ?? null,
    playerLoading: isLoading,
    /* v8 ignore start -- defensive fallback for non-Error query error */
    playerError: error ? (error instanceof Error ? error.message : 'Failed to load player') : null,
    /* v8 ignore stop */
    refreshPlayer,
    isSyncing,
    syncPhase: phase,
    backfillProgress,
    historyProgress,
    rivalsProgress,
    entriesFound,
    itemsCompleted,
    totalItems,
    currentSongName,
    seasonsQueried,
    rivalsFound,
    isThrottled,
    throttleStatusKey,
    pendingRankUpdate,
    estimatedRankUpdateMinutes,
    probeStatusKey,
    nextRetrySeconds,
    justCompleted: syncCompleted,
    clearCompleted: clearSyncCompleted,
    syncBannerDismissed,
    dismissSyncBanner,
  }), [data, isLoading, error, refreshPlayer, isSyncing, phase, backfillProgress, historyProgress, rivalsProgress, entriesFound, itemsCompleted, totalItems, currentSongName, seasonsQueried, rivalsFound, isThrottled, throttleStatusKey, pendingRankUpdate, estimatedRankUpdateMinutes, probeStatusKey, nextRetrySeconds, syncCompleted, clearSyncCompleted, syncBannerDismissed, dismissSyncBanner]);

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
