import { useState, useEffect, useCallback, useRef } from 'react';
import { api } from '../api/client';
import type { SyncStatusResponse } from '../models';

export type SyncPhase = 'idle' | 'backfill' | 'history' | 'complete' | 'error';

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

const POLL_INTERVAL_ACTIVE = 3000;   // 3s while syncing
const POLL_INTERVAL_IDLE = 60_000;   // 60s background heartbeat

export function useSyncStatus(accountId: string | undefined) {
  const [syncState, setSyncState] = useState<SyncState>({
    isSyncing: false,
    phase: 'idle',
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
    if (!accountId || !mountedRef.current) return;
    try {
      const res: SyncStatusResponse = await api.getSyncStatus(accountId);

      const bf = res.backfill;
      const hr = res.historyRecon;

      const bfStatus = bf?.status ?? null;
      const bfActive = bfStatus === 'pending' || bfStatus === 'in_progress';
      const bfProgress = bf && bf.totalSongsToCheck > 0
        ? bf.songsChecked / bf.totalSongsToCheck : 0;

      const hrStatus = hr?.status ?? null;
      const hrActive = hrStatus === 'pending' || hrStatus === 'in_progress';
      const hrProgress = hr && hr.totalSongsToProcess > 0
        ? hr.songsProcessed / hr.totalSongsToProcess : 0;

      const isSyncing = bfActive || hrActive;

      if (isSyncing) {
        wasSyncingRef.current = true;
      }

      let phase: SyncPhase;
      if (bfActive) phase = 'backfill';
      else if (hrActive) phase = 'history';
      else if (bfStatus === 'complete' || hrStatus === 'complete') phase = 'complete';
      else if (bfStatus === 'error' || hrStatus === 'error') phase = 'error';
      else phase = 'idle';

      if (!mountedRef.current) return;

      setSyncState({
        isSyncing,
        phase,
        backfillStatus: bfStatus,
        backfillProgress: bfProgress,
        historyStatus: hrStatus,
        historyProgress: hrProgress,
        entriesFound: bf?.entriesFound ?? 0,
      });

      if (!isSyncing && (phase === 'complete' || phase === 'error')) {
        // Sync finished — stop active polling entirely
        stopPolling();
        if (phase === 'complete' && wasSyncingRef.current) {
          setJustCompleted(true);
        }
        return; // Don't schedule another poll
      }

      // Schedule next poll: fast while syncing, slow when idle
      if (!document.hidden && mountedRef.current) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, isSyncing ? POLL_INTERVAL_ACTIVE : POLL_INTERVAL_IDLE);
      }
    } catch {
      // On error, retry after a longer delay
      if (!document.hidden && mountedRef.current) {
        stopPolling();
        pollRef.current = setTimeout(checkStatus, POLL_INTERVAL_IDLE);
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
      if (document.hidden) {
        stopPolling();
      } else {
        // Check immediately on return (this schedules the next poll if needed)
        void checkStatus();
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
  const progress = syncState.phase === 'backfill'
    ? syncState.backfillProgress * 0.5
    : syncState.phase === 'history'
      ? 0.5 + syncState.historyProgress * 0.5
      : syncState.phase === 'complete' ? 1 : 0;

  return { ...syncState, progress, justCompleted, clearCompleted };
}
