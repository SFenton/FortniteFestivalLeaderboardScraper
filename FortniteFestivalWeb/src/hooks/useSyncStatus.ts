import { useState, useEffect, useCallback, useRef } from 'react';
import { api } from '../api/client';
import type { SyncStatusResponse } from '../models';

type SyncState = {
  /** Whether we're actively syncing (backfill pending or in_progress) */
  isSyncing: boolean;
  /** Current backfill status string */
  status: string | null;
  /** Progress fraction 0..1 */
  progress: number;
  /** Number of new entries found */
  entriesFound: number;
};

const POLL_INTERVAL = 3000;

export function useSyncStatus(accountId: string | undefined) {
  const [syncState, setSyncState] = useState<SyncState>({
    isSyncing: false,
    status: null,
    progress: 0,
    entriesFound: 0,
  });
  const [justCompleted, setJustCompleted] = useState(false);
  const pollRef = useRef<ReturnType<typeof setInterval>>(null);
  const trackedRef = useRef(false);

  const checkStatus = useCallback(async () => {
    if (!accountId) return;
    try {
      const res: SyncStatusResponse = await api.getSyncStatus(accountId);
      if (!res.backfill) {
        setSyncState({ isSyncing: false, status: null, progress: 0, entriesFound: 0 });
        return;
      }
      const { status, songsChecked, totalSongsToCheck, entriesFound } = res.backfill;
      const isSyncing = status === 'pending' || status === 'in_progress';
      const progress = totalSongsToCheck > 0 ? songsChecked / totalSongsToCheck : 0;
      setSyncState({ isSyncing, status, progress, entriesFound });

      if (!isSyncing && (status === 'complete' || status === 'error')) {
        // Stop polling
        if (pollRef.current) {
          clearInterval(pollRef.current);
          pollRef.current = null;
        }
        if (status === 'complete') {
          setJustCompleted(true);
        }
      }
    } catch {
      // Silently ignore status check errors
    }
  }, [accountId]);

  // Track player and start polling on mount
  useEffect(() => {
    if (!accountId) return;
    trackedRef.current = false;
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
      pollRef.current = setInterval(checkStatus, POLL_INTERVAL);
    };

    void init();

    return () => {
      if (pollRef.current) {
        clearInterval(pollRef.current);
        pollRef.current = null;
      }
    };
  }, [accountId, checkStatus]);

  // Clear justCompleted after consumer reads it
  const clearCompleted = useCallback(() => setJustCompleted(false), []);

  return { ...syncState, justCompleted, clearCompleted };
}
