import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import { api } from '../../api/client';
import { type SyncStatusResponse, type SyncProgressMessage, type WsNotificationMessage } from '@festival/core/api/serverTypes';
import { SyncPhase, BackfillStatus } from '@festival/core';
import { useAppWebSocket } from './useAppWebSocket';
export type { SyncPhase };

type SyncState = {
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
};

import { SYNC_POLL_ACTIVE_MS, SYNC_POLL_IDLE_MS } from '@festival/theme';

/** How long to wait without a WS message before falling back to HTTP polling */
const WS_STALE_MS = 10_000;

export function useSyncStatus(accountId: string | undefined, options?: { track?: boolean }) {
  const track = options?.track ?? true;
  const [syncState, setSyncState] = useState<SyncState>({
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
  });
  const [justCompleted, setJustCompleted] = useState(false);
  const pollRef = useRef<ReturnType<typeof setTimeout>>(null);
  const wasSyncingRef = useRef(false);
  const syncKickedRef = useRef(false);
  const mountedRef = useRef(true);
  const lastWsMsgRef = useRef(0);
  const { subscribe, connected: wsConnected, send: wsSend } = useAppWebSocket();

  const stopPolling = useCallback(() => {
    if (pollRef.current) {
      clearTimeout(pollRef.current);
      pollRef.current = null;
    }
  }, []);

  // ── WebSocket message handler ──
  const handleWsMessage = useCallback((msg: WsNotificationMessage) => {
    if (!accountId || !mountedRef.current) return;

    if (msg.type === 'sync_progress') {
      const sp = msg as SyncProgressMessage;
      if (sp.accountId !== accountId) return;
      lastWsMsgRef.current = Date.now();

      const phaseMap: Record<string, SyncPhase> = {
        backfill: SyncPhase.Backfill,
        history: SyncPhase.History,
        rivals: SyncPhase.Rivals,
        complete: SyncPhase.Complete,
        error: SyncPhase.Error,
      };
      const phase = phaseMap[sp.phase] ?? SyncPhase.Idle;
      const isSyncing = phase === SyncPhase.Backfill || phase === SyncPhase.History || phase === SyncPhase.Rivals;
      const phaseProgress = sp.totalItems > 0 ? sp.itemsCompleted / sp.totalItems : 0;

      if (isSyncing) wasSyncingRef.current = true;

      setSyncState(prev => ({
        isSyncing,
        phase,
        backfillProgress: phase === SyncPhase.Backfill ? phaseProgress : (prev.backfillProgress > 0 ? 1 : prev.backfillProgress),
        historyProgress: phase === SyncPhase.History ? phaseProgress : (phase === SyncPhase.Rivals || phase === SyncPhase.Complete ? 1 : prev.historyProgress),
        rivalsProgress: phase === SyncPhase.Rivals ? phaseProgress : (phase === SyncPhase.Complete ? 1 : prev.rivalsProgress),
        entriesFound: sp.entriesFound,
        itemsCompleted: sp.itemsCompleted,
        totalItems: sp.totalItems,
        currentSongName: sp.currentSongName ?? null,
        seasonsQueried: sp.seasonsQueried ?? prev.seasonsQueried,
        rivalsFound: sp.rivalsFound ?? prev.rivalsFound,
      }));

      if (!isSyncing && phase === SyncPhase.Complete && (wasSyncingRef.current || syncKickedRef.current)) {
        setJustCompleted(true);
      }
      // While receiving WS messages, suppress HTTP polling
      stopPolling();
      return;
    }

    // Legacy completion events (backup)
    if (msg.type === 'backfill_complete' || msg.type === 'history_recon_complete' || msg.type === 'rivals_complete') {
      lastWsMsgRef.current = Date.now();
    }
  }, [accountId, stopPolling]);

  useEffect(() => {
    return subscribe(handleWsMessage);
  }, [subscribe, handleWsMessage]);

  // ── WebSocket account subscription ──
  // Tell the server which accountId this client cares about so per-account
  // sync_progress messages are routed to this connection.
  useEffect(() => {
    if (!accountId || !wsConnected) return;
    wsSend(JSON.stringify({ action: 'subscribe_sync', accountId }));
    return () => {
      wsSend(JSON.stringify({ action: 'unsubscribe_sync' }));
    };
  }, [accountId, wsConnected, wsSend]);

  // ── HTTP fallback polling ──
  /* v8 ignore start — async polling callback */
  const checkStatus = useCallback(async () => {
    /* v8 ignore start */
    if (!accountId || !mountedRef.current) return;
    /* v8 ignore stop */

    // Skip HTTP poll if we received a WS message recently AND the WS is still connected
    if (wsConnected && Date.now() - lastWsMsgRef.current < WS_STALE_MS && lastWsMsgRef.current > 0) {
      if (mountedRef.current) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, SYNC_POLL_ACTIVE_MS);
      }
      return;
    }

    try {
      const res: SyncStatusResponse = await api.getSyncStatus(accountId);

      const bf = res.backfill;
      const hr = res.historyRecon;
      const rv = res.rivals;

      const bfStatus = bf?.status ?? null;
      const bfActive = bfStatus === BackfillStatus.Pending || bfStatus === BackfillStatus.InProgress;
      const bfProgress = bf && bf.totalSongsToCheck > 0
        ? bf.songsChecked / bf.totalSongsToCheck : 0;

      const hrStatus = hr?.status ?? null;
      const hrActive = hrStatus === BackfillStatus.Pending || hrStatus === BackfillStatus.InProgress;
      const hrProgress = hr && hr.totalSongsToProcess > 0
        ? hr.songsProcessed / hr.totalSongsToProcess : 0;

      const rvStatus = rv?.status ?? null;
      const rvActive = rvStatus === BackfillStatus.Pending || rvStatus === BackfillStatus.InProgress;
      const rvProgress = rv && rv.totalCombosToCompute > 0
        ? rv.combosComputed / rv.totalCombosToCompute : 0;

      const isSyncing = bfActive || hrActive || rvActive;

      if (isSyncing) {
        wasSyncingRef.current = true;
      }

      let phase: SyncPhase;
      if (bfActive) phase = SyncPhase.Backfill;
      else if (hrActive) phase = SyncPhase.History;
      else if (rvActive) phase = SyncPhase.Rivals;
      else if (bfStatus === BackfillStatus.Complete || hrStatus === BackfillStatus.Complete) phase = SyncPhase.Complete;
      else if (bfStatus === BackfillStatus.Error || hrStatus === BackfillStatus.Error) phase = SyncPhase.Error;
      else phase = SyncPhase.Idle;

      /* v8 ignore start */
      if (!mountedRef.current) return;
      /* v8 ignore stop */

      setSyncState(prev => ({
        isSyncing,
        phase,
        backfillProgress: bfProgress,
        historyProgress: hrProgress,
        rivalsProgress: rvProgress,
        entriesFound: bf?.entriesFound ?? prev.entriesFound,
        itemsCompleted: bfActive ? (bf?.songsChecked ?? 0) : hrActive ? (hr?.songsProcessed ?? 0) : rvActive ? (rv?.combosComputed ?? 0) : prev.itemsCompleted,
        totalItems: bfActive ? (bf?.totalSongsToCheck ?? 0) : hrActive ? (hr?.totalSongsToProcess ?? 0) : rvActive ? (rv?.totalCombosToCompute ?? 0) : prev.totalItems,
        currentSongName: bf?.currentSongName ?? hr?.currentSongName ?? null,
        seasonsQueried: hr?.seasonsQueried ?? prev.seasonsQueried,
        rivalsFound: rv?.rivalsFound ?? prev.rivalsFound,
      }));

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
      if (!document.hidden && mountedRef.current) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, isSyncing ? SYNC_POLL_ACTIVE_MS : SYNC_POLL_IDLE_MS);
      }
    } catch {
      // On error, retry after a longer delay
      if (!document.hidden && mountedRef.current) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, SYNC_POLL_IDLE_MS);
      }
      /* v8 ignore stop */
    }
  }, [accountId, stopPolling, wsConnected]);

  // Track player and start polling on mount; pause when tab is hidden
  useEffect(() => {
    if (!accountId) return;
    mountedRef.current = true;
    wasSyncingRef.current = false;
    syncKickedRef.current = false;
    lastWsMsgRef.current = 0;
    setJustCompleted(false);

    const init = async () => {
      // Fire track request (idempotent) and capture whether a sync was kicked
      if (track) {
        try {
          const res = await api.trackPlayer(accountId);
          if (res.backfillKicked) syncKickedRef.current = true;
        } catch {
          // Ignore if track fails (e.g. in api-only mode without scraper)
        }
      }

      // Check status immediately (this schedules the next poll if needed)
      await checkStatus();
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
  const progress = useMemo(() => {
    switch (syncState.phase) {
      case SyncPhase.Backfill:
        return syncState.backfillProgress * (1 / 3);
      case SyncPhase.History:
        return (1 / 3) + syncState.historyProgress * (1 / 3);
      case SyncPhase.Rivals:
        return (2 / 3) + syncState.rivalsProgress * (1 / 3);
      case SyncPhase.Complete:
        return 1;
      default:
        return 0;
    }
  }, [syncState.phase, syncState.backfillProgress, syncState.historyProgress, syncState.rivalsProgress]);

  return useMemo(
    () => ({ ...syncState, progress, justCompleted, clearCompleted }),
    [syncState, progress, justCompleted, clearCompleted],
  );
}
