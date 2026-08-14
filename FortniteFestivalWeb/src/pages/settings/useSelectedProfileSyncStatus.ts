import { useEffect, useState } from 'react';
import type { BandSyncStatusResponse, SyncStatusResponse } from '@festival/core/api';
import type { SelectedProfile } from '../../hooks/data/useSelectedProfile';
import { api } from '../../api/client';

export const PROFILE_SYNC_STATUS_POLL_MS = 5_000;

export function useSelectedProfileSyncStatus(
  selectedProfile: SelectedProfile | null,
): {
  playerStatus: SyncStatusResponse | null;
  bandStatus: BandSyncStatusResponse | null;
  loadFailed: boolean;
} {
  const [playerStatus, setPlayerStatus] = useState<SyncStatusResponse | null>(null);
  const [bandStatus, setBandStatus] = useState<BandSyncStatusResponse | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const playerAccountId = selectedProfile?.type === 'player'
    ? selectedProfile.accountId
    : null;
  const bandType = selectedProfile?.type === 'band'
    ? selectedProfile.bandType
    : null;
  const bandTeamKey = selectedProfile?.type === 'band'
    ? selectedProfile.teamKey
    : null;

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;

    setPlayerStatus(null);
    setBandStatus(null);
    setLoadFailed(false);
    if (!selectedProfile) return () => controller.abort();

    const load = async () => {
      try {
        if (playerAccountId) {
          const data = await api.getSyncStatus(playerAccountId, {
            signal: controller.signal,
          });
          if (controller.signal.aborted) return;
          setPlayerStatus(data);
          setBandStatus(null);
        } else if (bandType && bandTeamKey) {
          const data = await api.getBandSyncStatus(bandType, bandTeamKey, {
            signal: controller.signal,
          });
          if (controller.signal.aborted) return;
          setBandStatus(data);
          setPlayerStatus(null);
        }
        if (!controller.signal.aborted) setLoadFailed(false);
      } catch {
        if (!controller.signal.aborted) setLoadFailed(true);
      } finally {
        if (!controller.signal.aborted) {
          timer = setTimeout(load, PROFILE_SYNC_STATUS_POLL_MS);
        }
      }
    };

    void load();
    return () => {
      controller.abort();
      if (timer) clearTimeout(timer);
    };
  }, [bandTeamKey, bandType, playerAccountId, selectedProfile]);

  return { playerStatus, bandStatus, loadFailed };
}
