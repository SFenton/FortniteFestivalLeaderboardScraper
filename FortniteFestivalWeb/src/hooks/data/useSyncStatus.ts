import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import { api } from '../../api/client';
import { type SyncStatusResponse } from '@festival/core/api/serverTypes';
import { SyncPhase, BackfillStatus } from '@festival/core';
export type { SyncPhase };

type SyncState = {
  /** Whether we're actively syncing (any phase in progress) */
  isSyncing: boolean;
  /** Current phase: backfill, history, complete, idle */
  phase: SyncPhase;
  /** Backfill status string */
  backfillStatus: string | null;
  /** Backfill progress 0..1 */
  backfillProgress: number;
  /** History recon status string */
  historyStatus: string | null;
  /** History recon progress 0..1 */
  historyProgress: number;
  /** Number of new entries found */
  entriesFound: number;
};

import { SYNC_POLL_ACTIVE_MS, SYNC_POLL_IDLE_MS } from '@festival/theme';

export function useSyncStatus(accountId: string | undefined) {
  const [syncState, setSyncState] = useState<SyncState>({
    isSyncing: false,
    phase: SyncPhase.Idle,
    backfillStatus: null,
    backfillProgress: 0,
    historyStatus: null,
    historyProgress: 0,
    entriesFound: 0,
  });
  const [justCompleted, setJustCompleted] = useState(false);
  const pollRef = useRef<ReturnType<typeof setTimeout>>(null);
  const trackedRef = useRef(false);
  const wasSyncingRef = useRef(false);
  const mountedRef = useRef(true);

  const stopPolling = useCallback(() => {
    if (pollRef.current) {
      clearTimeout(pollRef.current);
      pollRef.current = null;
    }
  }, []);

  const checkStatus = useCallback(async () => {
    /* v8 ignore start */
    if (!accountId || !mountedRef.current) return;
    /* v8 ignore stop */
    try {
      const res: SyncStatusResponse = await api.getSyncStatus(accountId);

      const bf = res.backfill;
      const hr = res.historyRecon;

      const bfStatus = bf?.status ?? null;
      const bfActive = bfStatus === BackfillStatus.Pending || bfStatus === BackfillStatus.InProgress;
      const bfProgress = bf && bf.totalSongsToCheck > 0
        ? bf.songsChecked / bf.totalSongsToCheck : 0;

      const hrStatus = hr?.status ?? null;
      const hrActive = hrStatus === BackfillStatus.Pending || hrStatus === BackfillStatus.InProgress;
      const hrProgress = hr && hr.totalSongsToProcess > 0
        ? hr.songsProcessed / hr.totalSongsToProcess : 0;

      const isSyncing = bfActive || hrActive;

      if (isSyncing) {
        wasSyncingRef.current = true;
      }

      let phase: SyncPhase;
      if (bfActive) phase = SyncPhase.Backfill;
      else if (hrActive) phase = SyncPhase.History;
      else if (bfStatus === BackfillStatus.Complete || hrStatus === BackfillStatus.Complete) phase = SyncPhase.Complete;
      else if (bfStatus === BackfillStatus.Error || hrStatus === BackfillStatus.Error) phase = SyncPhase.Error;
      else phase = SyncPhase.Idle;

      /* v8 ignore start */
      if (!mountedRef.current) return;
      /* v8 ignore stop */

      setSyncState({
        isSyncing,
        phase,
        backfillStatus: bfStatus,
        backfillProgress: bfProgress,
        historyStatus: hrStatus,
        historyProgress: hrProgress,
        entriesFound: bf?.entriesFound ?? 0,
      });

      if (!isSyncing && (phase === SyncPhase.Complete || phase === SyncPhase.Error)) {
        // Sync finished — stop active polling entirely
        stopPolling();
        if (phase === SyncPhase.Complete && wasSyncingRef.current) {
          /* v8 ignore start */
          setJustCompleted(true);
          /* v8 ignore stop */
        }
        return; // Don't schedule another poll
      }

      // Schedule next poll: fast while syncing, slow when idle
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
    }
  }, [accountId, stopPolling]);

  // Track player and start polling on mount; pause when tab is hidden
  useEffect(() => {
    if (!accountId) return;
    mountedRef.current = true;
    trackedRef.current = false;
    wasSyncingRef.current = false;
    setJustCompleted(false);

    const init = async () => {
      // Fire track request (idempotent)
      try {
        await api.trackPlayer(accountId);
      } catch {
        // Ignore if track fails (e.g. in api-only mode without scraper)
      }
      trackedRef.current = true;

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
  }, [accountId, checkStatus, stopPolling]);

  // Clear justCompleted after consumer reads it
  const clearCompleted = useCallback(() => setJustCompleted(false), []);

  // Backwards-compat: expose combined progress (0..1)
  const progress = useMemo(() =>
    syncState.phase === SyncPhase.Backfill
      ? syncState.backfillProgress * 0.5
      : syncState.phase === SyncPhase.History
        ? 0.5 + syncState.historyProgress * 0.5
        : syncState.phase === SyncPhase.Complete ? 1 : 0,
    [syncState.phase, syncState.backfillProgress, syncState.historyProgress],
  );

  return useMemo(
    () => ({ ...syncState, progress, justCompleted, clearCompleted }),
    [syncState, progress, justCompleted, clearCompleted],
  );
}
