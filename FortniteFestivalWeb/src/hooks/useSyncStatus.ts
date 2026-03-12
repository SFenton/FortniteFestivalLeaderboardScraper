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

const POLL_INTERVAL = 3000;

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
  const pollRef = useRef<ReturnType<typeof setInterval>>(null);
  const trackedRef = useRef(false);
  const wasSyncingRef = useRef(false);

  const checkStatus = useCallback(async () => {
    if (!accountId) return;
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
        if (pollRef.current) {
          clearInterval(pollRef.current);
          pollRef.current = null;
        }
        // Only signal completion if we actually observed an active sync
        if (phase === 'complete' && wasSyncingRef.current) {
          setJustCompleted(true);
        }
      }
    } catch {
      // Silently ignore status check errors
    }
  }, [accountId]);

  // Track player and start polling on mount; pause when tab is hidden
  useEffect(() => {
    if (!accountId) return;
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

      // Check status immediately
      await checkStatus();

      // Start polling
      startPolling();
    };

    const startPolling = () => {
      if (!pollRef.current) {
        pollRef.current = setInterval(checkStatus, POLL_INTERVAL);
      }
    };
    const stopPolling = () => {
      if (pollRef.current) {
        clearInterval(pollRef.current);
        pollRef.current = null;
      }
    };
    const onVisibility = () => {
      if (document.hidden) {
        stopPolling();
      } else {
        // Check immediately on return, then resume interval
        void checkStatus();
        startPolling();
      }
    };

    document.addEventListener('visibilitychange', onVisibility);
    void init();

    return () => {
      stopPolling();
      document.removeEventListener('visibilitychange', onVisibility);
    };
  }, [accountId, checkStatus]);

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
