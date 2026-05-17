import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import { api } from '../../api/client';
import { type SyncStatusResponse, type SyncProgressMessage, type WsNotificationMessage } from '@festival/core/api/serverTypes';
import { SyncPhase, BackfillStatus } from '@festival/core';
import { useAppWebSocket } from './useAppWebSocket';
export type { SyncPhase };

type SyncState = {
  /** Whether the backend knows this account as a tracked/registered player. */
  isTracked: boolean;
  /** True after the first sync-status response for this account has loaded. */
  syncStatusLoaded: boolean;
  /** Whether we're actively syncing (any phase in progress) */
  isSyncing: boolean;
  /** Current phase: backfill, history, rivals, complete, idle */
  phase: SyncPhase;
  /** Backfill progress 0..1 */
  backfillProgress: number;
  /** History recon progress 0..1 */
  historyProgress: number;
  /** Rivals progress 0..1 */
  rivalsProgress: number;
  /** Number of new entries found */
  entriesFound: number;
  /** Items completed in current phase */
  itemsCompleted: number;
  /** Total items to process in current phase */
  totalItems: number;
  /** Current song being processed */
  currentSongName: string | null;
  /** Number of seasons queried (history phase) */
  seasonsQueried: number;
  /** Number of rivals found (rivals phase) */
  rivalsFound: number;
  /** Whether the CDN limiter has significantly reduced DOP */
  isThrottled: boolean;
  /** Status key for throttle reason (frontend translates) */
  throttleStatusKey: string | null;
  /** True when sync is complete but global ranks not yet recalculated */
  pendingRankUpdate: boolean;
  /** Estimated minutes until next global ranking pass */
  estimatedRankUpdateMinutes: number | null;
  /** CDN probe status key (e.g. "probe_retrying", "probe_waiting") */
  probeStatusKey: string | null;
  /** Seconds until next probe retry */
  nextRetrySeconds: number | null;
};

const idleSyncState: SyncState = {
  isTracked: false,
  syncStatusLoaded: false,
  isSyncing: false,
  phase: SyncPhase.Idle,
  backfillProgress: 0,
  historyProgress: 0,
  rivalsProgress: 0,
  entriesFound: 0,
  itemsCompleted: 0,
  totalItems: 0,
  currentSongName: null,
  seasonsQueried: 0,
  rivalsFound: 0,
  isThrottled: false,
  throttleStatusKey: null,
  pendingRankUpdate: false,
  estimatedRankUpdateMinutes: null,
  probeStatusKey: null,
  nextRetrySeconds: null,
};

function createIdleSyncState(): SyncState {
  return { ...idleSyncState };
}

import { SYNC_POLL_ACTIVE_MS, SYNC_POLL_IDLE_MS } from '@festival/theme';

/** How long to wait without a WS message before falling back to HTTP polling */
const WS_STALE_MS = 10_000;

function resolveDisplayProgress(
  rawCompleted: number,
  rawTotal: number,
  displayCompleted?: number | null,
  displayTotal?: number | null,
): { completed: number; total: number } {
  const completed = typeof displayCompleted === 'number' ? displayCompleted : rawCompleted;
  const total = typeof displayTotal === 'number' ? displayTotal : rawTotal;
  return {
    completed: Math.max(0, completed),
    total: Math.max(0, total),
  };
}

function deriveSyncStateFromStatus(res: SyncStatusResponse, prev: SyncState): SyncState {
  const bf = res.backfill;
  const hr = res.historyRecon;
  const rv = res.rivals;
  const ps = res.postScrape;

  const bfStatus = bf?.status ?? null;
  const bfDeferred = bfStatus === BackfillStatus.Deferred;
  const bfActive = bfStatus === BackfillStatus.Pending || bfStatus === BackfillStatus.InProgress;
  const bfDisplay = resolveDisplayProgress(
    bf?.songsChecked ?? 0,
    bf?.totalSongsToCheck ?? 0,
    bf?.displaySongsChecked,
    bf?.displayTotalSongs,
  );
  const bfProgress = bfDisplay.total > 0
    ? Math.min(bfDisplay.completed / bfDisplay.total, 1) : 0;

  const hrStatus = hr?.status ?? null;
  const hrActive = hrStatus === BackfillStatus.Pending || hrStatus === BackfillStatus.InProgress;
  const hrProgress = hr && hr.totalSongsToProcess > 0
    ? Math.min(hr.songsProcessed / hr.totalSongsToProcess, 1) : 0;

  const rvStatus = rv?.status ?? null;
  const rvActive = rvStatus === BackfillStatus.Pending || rvStatus === BackfillStatus.InProgress;
  const rvProgress = rv && rv.totalCombosToCompute > 0
    ? Math.min(rv.combosComputed / rv.totalCombosToCompute, 1) : 0;

  const psActive = ps?.status === BackfillStatus.Pending || ps?.status === BackfillStatus.InProgress;
  const isSyncing = bfDeferred || bfActive || hrActive || rvActive || psActive;

  let phase: SyncPhase;
  if (bfDeferred) phase = SyncPhase.Queued;
  else if (bfActive) phase = SyncPhase.Backfill;
  else if (hrActive) phase = SyncPhase.History;
  else if (rvActive) phase = SyncPhase.Rivals;
  else if (psActive) phase = SyncPhase.PostScrape;
  else if (bfStatus === BackfillStatus.Complete || hrStatus === BackfillStatus.Complete) phase = SyncPhase.Complete;
  else if (bfStatus === BackfillStatus.Error || hrStatus === BackfillStatus.Error) phase = SyncPhase.Error;
  else phase = SyncPhase.Idle;

  return {
    isTracked: !!res.isTracked,
    syncStatusLoaded: true,
    isSyncing,
    phase,
    backfillProgress: bfDeferred ? 0 : bfProgress,
    historyProgress: hrProgress,
    rivalsProgress: rvProgress,
    entriesFound: psActive ? (ps?.entriesFound ?? prev.entriesFound) : (bf?.entriesFound ?? prev.entriesFound),
    itemsCompleted: bfActive ? bfDisplay.completed : hrActive ? (hr?.songsProcessed ?? 0) : rvActive ? (rv?.combosComputed ?? 0) : psActive ? (ps?.itemsCompleted ?? 0) : prev.itemsCompleted,
    totalItems: bfDeferred ? bfDisplay.total : bfActive ? bfDisplay.total : hrActive ? (hr?.totalSongsToProcess ?? 0) : rvActive ? (rv?.totalCombosToCompute ?? 0) : psActive ? (ps?.totalItems ?? 0) : prev.totalItems,
    currentSongName: ps?.currentSongName ?? bf?.currentSongName ?? hr?.currentSongName ?? null,
    seasonsQueried: hr?.seasonsQueried ?? prev.seasonsQueried,
    rivalsFound: rv?.rivalsFound ?? prev.rivalsFound,
    isThrottled: false,
    throttleStatusKey: null,
    pendingRankUpdate: res.pendingRankUpdate ?? bf?.rankingsPending ?? false,
    estimatedRankUpdateMinutes: null,
    probeStatusKey: null,
    nextRetrySeconds: null,
  };
}

function getSyncPhaseFromStatus(res: SyncStatusResponse): SyncPhase {
  return deriveSyncStateFromStatus(res, createIdleSyncState()).phase;
}

function isSyncingStatus(res: SyncStatusResponse): boolean {
  return deriveSyncStateFromStatus(res, createIdleSyncState()).isSyncing;
}

function shouldOptimisticallyQueueAfterStatus(res: SyncStatusResponse): boolean {
  if (res.isTracked || isSyncingStatus(res)) return false;
  const phase = getSyncPhaseFromStatus(res);
  return phase !== SyncPhase.Complete;
}

export function useSyncStatus(accountId: string | undefined, options?: { track?: boolean; useWebSocket?: boolean }) {
  const track = options?.track ?? true;
  const useWebSocket = options?.useWebSocket ?? true;
  const [syncState, setSyncState] = useState<SyncState>(createIdleSyncState);
  const [syncStateAccountId, setSyncStateAccountId] = useState<string | null>(() => accountId ?? null);
  const [justCompleted, setJustCompleted] = useState(false);
  const pollRef = useRef<ReturnType<typeof setTimeout>>(null);
  const wasSyncingRef = useRef(false);
  const syncKickedRef = useRef(false);
  const mountedRef = useRef(true);
  const lastWsMsgRef = useRef(0);
  const desiredAccountRef = useRef<string | null>(accountId ?? null);
  const hasSyncSubscriptionRef = useRef(false);
  const checkStatusRef = useRef<(() => void) | null>(null);
  /** Timestamp (ms) until which probe status is locked to prevent blips */
  const probeLockedUntilRef = useRef(0);
  const { subscribe, connected: wsConnected, send: wsSend, subscribeOpen } = useAppWebSocket();

  const stopPolling = useCallback(() => {
    if (pollRef.current) {
      clearTimeout(pollRef.current);
      pollRef.current = null;
    }
  }, []);

  const resetSyncTracking = useCallback((nextAccountId: string | null) => {
    desiredAccountRef.current = nextAccountId;
    stopPolling();
    wasSyncingRef.current = false;
    syncKickedRef.current = false;
    lastWsMsgRef.current = 0;
    probeLockedUntilRef.current = 0;
    setJustCompleted(false);
    setSyncStateAccountId(nextAccountId);
    setSyncState(createIdleSyncState());
  }, [stopPolling]);

  // ── WebSocket message handler ──
  const handleWsMessage = useCallback((msg: WsNotificationMessage) => {
    if (!accountId || !mountedRef.current || desiredAccountRef.current !== accountId) return;

    if (msg.type === 'sync_progress') {
      const sp = msg as SyncProgressMessage;
      if (sp.accountId !== accountId) return;

      const phaseMap: Record<string, SyncPhase> = {
        queued: SyncPhase.Queued,
        backfill: SyncPhase.Backfill,
        history: SyncPhase.History,
        rivals: SyncPhase.Rivals,
        postscrape: SyncPhase.PostScrape,
        complete: SyncPhase.Complete,
        error: SyncPhase.Error,
      };
      const phase = phaseMap[sp.phase] ?? SyncPhase.Idle;
      lastWsMsgRef.current = phase === SyncPhase.Queued ? 0 : Date.now();
      const isSyncing = phase === SyncPhase.Queued || phase === SyncPhase.Backfill || phase === SyncPhase.History || phase === SyncPhase.Rivals || phase === SyncPhase.PostScrape;
      const displayProgress = resolveDisplayProgress(sp.itemsCompleted, sp.totalItems, sp.displayItemsCompleted, sp.displayTotalItems);
      const phaseProgress = displayProgress.total > 0 ? Math.min(displayProgress.completed / displayProgress.total, 1) : 0;

      if (isSyncing) wasSyncingRef.current = true;

      setSyncState(prev => {
        // 3s minimum display for probe states to prevent blips
        const now = Date.now();
        const incomingProbe = sp.probeStatusKey ?? null;
        const lockActive = now < probeLockedUntilRef.current;
        let effectiveProbe: string | null;
        let effectiveRetry: number | null;

        if (incomingProbe) {
          // New probe state — set/extend lock
          probeLockedUntilRef.current = now + 3000;
          effectiveProbe = incomingProbe;
          effectiveRetry = sp.nextRetrySeconds ?? null;
        } else if (lockActive) {
          // Lock still active — keep previous probe display
          effectiveProbe = prev.probeStatusKey;
          effectiveRetry = prev.nextRetrySeconds;
        } else {
          // No probe, lock expired — clear
          effectiveProbe = null;
          effectiveRetry = null;
        }

        return {
          isTracked: prev.isTracked,
          syncStatusLoaded: prev.syncStatusLoaded,
          isSyncing,
          phase,
          backfillProgress: phase === SyncPhase.Backfill ? phaseProgress : (phase === SyncPhase.Queued ? 0 : (prev.backfillProgress > 0 ? 1 : prev.backfillProgress)),
          historyProgress: phase === SyncPhase.History ? phaseProgress : (phase === SyncPhase.Rivals || phase === SyncPhase.Complete ? 1 : prev.historyProgress),
          rivalsProgress: phase === SyncPhase.Rivals ? phaseProgress : (phase === SyncPhase.Complete ? 1 : prev.rivalsProgress),
          entriesFound: sp.entriesFound,
          itemsCompleted: displayProgress.completed,
          totalItems: displayProgress.total,
          currentSongName: sp.currentSongName ?? null,
          seasonsQueried: sp.seasonsQueried ?? prev.seasonsQueried,
          rivalsFound: sp.rivalsFound ?? prev.rivalsFound,
          isThrottled: sp.isThrottled ?? false,
          throttleStatusKey: sp.throttleStatusKey ?? null,
          pendingRankUpdate: sp.pendingRankUpdate ?? false,
          estimatedRankUpdateMinutes: sp.estimatedRankUpdateMinutes ?? null,
          probeStatusKey: effectiveProbe,
          nextRetrySeconds: effectiveRetry,
        };
      });

      if (!isSyncing && phase === SyncPhase.Complete && (wasSyncingRef.current || syncKickedRef.current)) {
        setJustCompleted(true);
      }
      stopPolling();
      if (!document.hidden && mountedRef.current && desiredAccountRef.current === accountId) {
        const delay = phase === SyncPhase.Queued ? SYNC_POLL_ACTIVE_MS : WS_STALE_MS;
        pollRef.current = setTimeout(() => { checkStatusRef.current?.(); }, delay);
      }
      return;
    }

    // Legacy completion events (backup)
    if (msg.type === 'backfill_complete' || msg.type === 'history_recon_complete' || msg.type === 'rivals_complete') {
      lastWsMsgRef.current = Date.now();
    }
  }, [accountId, stopPolling]);

  useEffect(() => {
    if (!useWebSocket) return;
    return subscribe(handleWsMessage);
  }, [useWebSocket, subscribe, handleWsMessage]);

  // ── WebSocket account subscription ──
  // Tell the server which accountId this client cares about so per-account
  // sync_progress messages are routed to this connection.
  useEffect(() => {
    resetSyncTracking(accountId ?? null);
  }, [accountId, resetSyncTracking]);

  const replayAccountSubscription = useCallback(() => {
    if (!useWebSocket) return;
    const desiredAccount = desiredAccountRef.current;
    if (!desiredAccount) return;
    wsSend(JSON.stringify({ action: 'subscribe_sync', accountId: desiredAccount }));
    hasSyncSubscriptionRef.current = true;
  }, [useWebSocket, wsSend]);

  useEffect(() => {
    if (!useWebSocket) return;
    return subscribeOpen(replayAccountSubscription);
  }, [useWebSocket, subscribeOpen, replayAccountSubscription]);

  useEffect(() => {
    desiredAccountRef.current = accountId ?? null;
    if (!useWebSocket || !wsConnected) return;

    if (accountId) {
      wsSend(JSON.stringify({ action: 'subscribe_sync', accountId }));
      hasSyncSubscriptionRef.current = true;
      return;
    }

    if (!hasSyncSubscriptionRef.current) return;

    wsSend(JSON.stringify({ action: 'unsubscribe_sync' }));
    hasSyncSubscriptionRef.current = false;
  }, [accountId, useWebSocket, wsConnected, wsSend]);

  useEffect(() => {
    return () => {
      desiredAccountRef.current = null;
      if (!useWebSocket || !hasSyncSubscriptionRef.current) return;
      wsSend(JSON.stringify({ action: 'unsubscribe_sync' }));
      hasSyncSubscriptionRef.current = false;
    };
  }, [useWebSocket, wsSend]);

  // ── HTTP fallback polling ──
  /* v8 ignore start — async polling callback */
  const checkStatus = useCallback(async () => {
    /* v8 ignore start */
    const requestedAccountId = accountId;
    if (!requestedAccountId || !mountedRef.current || desiredAccountRef.current !== requestedAccountId) return;
    /* v8 ignore stop */

    // Skip HTTP poll if we received a WS message recently AND the WS is still connected
    if (useWebSocket && wsConnected && Date.now() - lastWsMsgRef.current < WS_STALE_MS && lastWsMsgRef.current > 0) {
      if (mountedRef.current) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, SYNC_POLL_ACTIVE_MS);
      }
      return;
    }

    try {
      const res: SyncStatusResponse = await api.getSyncStatus(requestedAccountId);
      const isSyncing = isSyncingStatus(res);

      if (isSyncing) {
        wasSyncingRef.current = true;
      }
      const phase = getSyncPhaseFromStatus(res);

      /* v8 ignore start */
      if (!mountedRef.current || desiredAccountRef.current !== requestedAccountId) return;
      /* v8 ignore stop */

      setSyncState(prev => deriveSyncStateFromStatus(res, prev));

      if (!isSyncing && (phase === SyncPhase.Complete || phase === SyncPhase.Error)) {
        // Sync finished — stop active polling entirely
        stopPolling();
        if (phase === SyncPhase.Complete && (wasSyncingRef.current || syncKickedRef.current)) {
          /* v8 ignore start */
          setJustCompleted(true);
          /* v8 ignore stop */
        }
        return; // Don't schedule another poll
      }

      // Schedule next poll: fast while syncing, slow when idle
      /* v8 ignore start — timer scheduling + document.hidden: DOM visibility API */
      if (!document.hidden && mountedRef.current && desiredAccountRef.current === requestedAccountId) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, isSyncing ? SYNC_POLL_ACTIVE_MS : SYNC_POLL_IDLE_MS);
      }
    } catch {
      // On error, retry after a longer delay
      if (!document.hidden && mountedRef.current && desiredAccountRef.current === requestedAccountId) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, SYNC_POLL_IDLE_MS);
      }
      /* v8 ignore stop */
    }
  }, [accountId, stopPolling, useWebSocket, wsConnected]);

  useEffect(() => {
    checkStatusRef.current = checkStatus;
  }, [checkStatus]);

  // Track player and start polling on mount; pause when tab is hidden
  useEffect(() => {
    if (!accountId) {
      mountedRef.current = false;
      return;
    }
    const requestedAccountId = accountId;
    mountedRef.current = true;

    const init = async () => {
      let preflightStatus: SyncStatusResponse | null = null;
      let preflightPhase = SyncPhase.Idle;
      let preflightWasSyncing = false;
      let optimisticQueued = false;

      // Fast read first: completed/tracked profiles should not flash a queued card.
      if (track) {
        try {
          preflightStatus = await api.getSyncStatus(requestedAccountId);
          if (!mountedRef.current || desiredAccountRef.current !== requestedAccountId) return;

          preflightPhase = getSyncPhaseFromStatus(preflightStatus);
          preflightWasSyncing = isSyncingStatus(preflightStatus);
          if (preflightWasSyncing) wasSyncingRef.current = true;

          setSyncState(prev => deriveSyncStateFromStatus(preflightStatus!, prev));

          if (shouldOptimisticallyQueueAfterStatus(preflightStatus)) {
            optimisticQueued = true;
            syncKickedRef.current = true;
            wasSyncingRef.current = true;
            setSyncState(prev => ({
              ...prev,
              isSyncing: true,
              phase: SyncPhase.Queued,
              backfillProgress: 0,
              historyProgress: 0,
              rivalsProgress: 0,
              entriesFound: 0,
              itemsCompleted: 0,
              totalItems: 0,
              currentSongName: null,
              isThrottled: false,
              throttleStatusKey: null,
              probeStatusKey: null,
              nextRetrySeconds: null,
            }));
          }
        } catch {
          preflightStatus = null;
        }

        try {
          const res = await api.trackPlayer(requestedAccountId);
          if (!mountedRef.current || desiredAccountRef.current !== requestedAccountId) return;
          if (res.syncDeferred) {
            syncKickedRef.current = true;
            wasSyncingRef.current = true;
            setSyncState(prev => ({
              ...prev,
              isTracked: true,
              isSyncing: true,
              phase: SyncPhase.Queued,
              pendingRankUpdate: res.pendingRankUpdate ?? prev.pendingRankUpdate,
            }));
          } else if (res.backfillKicked) {
            syncKickedRef.current = true;
            wasSyncingRef.current = true;
            // Optimistic: show banner immediately so fast backfills don't race
            // past the first HTTP poll without the user ever seeing the banner.
            setSyncState(prev => ({
              ...prev,
              isTracked: true,
              isSyncing: true,
              phase: SyncPhase.Backfill,
            }));
          } else if (optimisticQueued) {
            syncKickedRef.current = false;
            wasSyncingRef.current = preflightWasSyncing;
            if (preflightStatus) {
              setSyncState(prev => deriveSyncStateFromStatus(preflightStatus!, prev));
            } else {
              setSyncState(createIdleSyncState());
            }
          }
        } catch {
          if (optimisticQueued) {
            syncKickedRef.current = false;
            wasSyncingRef.current = preflightWasSyncing;
            if (preflightStatus) {
              setSyncState(prev => deriveSyncStateFromStatus(preflightStatus!, prev));
            } else {
              setSyncState(createIdleSyncState());
            }
          }
        }
      }

      if (!mountedRef.current || desiredAccountRef.current !== requestedAccountId) return;

      // If backfill was just kicked, the backend hasn't registered live progress
      // yet (syncTracker.BeginBackfill runs inside Task.Run). An immediate
      // checkStatus() would hit the stale precomputed cache and overwrite our
      // optimistic isSyncing=true. Defer the first poll so the backend has time
      // to register, preserving the optimistic banner.
      if (syncKickedRef.current) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, SYNC_POLL_ACTIVE_MS);
      } else if (preflightStatus) {
        stopPolling();
        if (preflightPhase !== SyncPhase.Complete && preflightPhase !== SyncPhase.Error && !document.hidden && mountedRef.current && desiredAccountRef.current === requestedAccountId) {
          pollRef.current = setTimeout(checkStatus, preflightWasSyncing ? SYNC_POLL_ACTIVE_MS : SYNC_POLL_IDLE_MS);
        }
      } else {
        await checkStatus();
      }
    };

    const onVisibility = () => {
      /* v8 ignore start */
      if (document.hidden) {
        stopPolling();
      } else {
        // Check immediately on return (this schedules the next poll if needed)
        void checkStatus();
      /* v8 ignore stop */
      }
    };

    document.addEventListener('visibilitychange', onVisibility);
    void init();

    return () => {
      mountedRef.current = false;
      stopPolling();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [accountId, track, checkStatus, stopPolling]);

  // Clear justCompleted after consumer reads it
  const clearCompleted = useCallback(() => setJustCompleted(false), []);

  // Combined progress (0..1) across all 3 phases (equally weighted at 1/3 each)
  // PostScrape is standalone and uses its own itemsCompleted/totalItems ratio
  const effectiveSyncState = accountId && syncStateAccountId === accountId ? syncState : idleSyncState;
  const effectiveJustCompleted = accountId && syncStateAccountId === accountId ? justCompleted : false;

  const progress = useMemo(() => {
    switch (effectiveSyncState.phase) {
      case SyncPhase.Backfill:
        return effectiveSyncState.backfillProgress * (1 / 3);
      case SyncPhase.Queued:
        return 0;
      case SyncPhase.History:
        return (1 / 3) + effectiveSyncState.historyProgress * (1 / 3);
      case SyncPhase.Rivals:
        return (2 / 3) + effectiveSyncState.rivalsProgress * (1 / 3);
      case SyncPhase.PostScrape:
        return effectiveSyncState.totalItems > 0 ? effectiveSyncState.itemsCompleted / effectiveSyncState.totalItems : 0;
      case SyncPhase.Complete:
        return 1;
      default:
        return 0;
    }
  }, [effectiveSyncState.phase, effectiveSyncState.backfillProgress, effectiveSyncState.historyProgress, effectiveSyncState.rivalsProgress, effectiveSyncState.itemsCompleted, effectiveSyncState.totalItems]);

  return useMemo(
    () => ({ ...effectiveSyncState, progress, justCompleted: effectiveJustCompleted, clearCompleted }),
    [effectiveSyncState, progress, effectiveJustCompleted, clearCompleted],
  );
}
