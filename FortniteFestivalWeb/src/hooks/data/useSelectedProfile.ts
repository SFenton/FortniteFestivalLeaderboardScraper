import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  LEGACY_TRACKED_PLAYER_STORAGE_KEY,
  LEGACY_TRACKED_PLAYER_SYNC_EVENT,
  SELECTED_PROFILE_STORAGE_KEY,
  SELECTED_PROFILE_SYNC_EVENT,
  clearSelectedProfileStorage,
  readSelectedProfile,
  writeSelectedProfile,
  type SelectedBandProfile,
  type SelectedBandMemberProfile,
  type SelectedPlayerProfile,
  type SelectedProfile,
} from '../../state/selectedProfile';

export type { SelectedBandProfile, SelectedBandMemberProfile, SelectedPlayerProfile, SelectedProfile };

export type SelectBandProfileInput = Omit<SelectedBandProfile, 'type' | 'members'> & {
  members?: SelectedBandMemberProfile[];
};

export function useSelectedProfile() {
  const [profile, setProfileState] = useState<SelectedProfile | null>(readSelectedProfile);

  const setProfile = useCallback((nextProfile: SelectedProfile) => {
    setProfileState(writeSelectedProfile(nextProfile));
  }, []);

  const selectPlayer = useCallback((player: Omit<SelectedPlayerProfile, 'type'>) => {
    setProfile({ type: 'player', ...player });
  }, [setProfile]);

  const selectBand = useCallback((band: SelectBandProfileInput) => {
    setProfile({ type: 'band', ...band, members: band.members ?? [] });
  }, [setProfile]);

  const clearSelectedProfile = useCallback(() => {
    clearSelectedProfileStorage();
    setProfileState(null);
  }, []);

  useEffect(() => {
    const sync = () => setProfileState(readSelectedProfile());
    const syncStorage = (event: StorageEvent) => {
      if (event.key === SELECTED_PROFILE_STORAGE_KEY || event.key === LEGACY_TRACKED_PLAYER_STORAGE_KEY) sync();
    };

    window.addEventListener(SELECTED_PROFILE_SYNC_EVENT, sync);
    window.addEventListener(LEGACY_TRACKED_PLAYER_SYNC_EVENT, sync);
    window.addEventListener('storage', syncStorage);
    return () => {
      window.removeEventListener(SELECTED_PROFILE_SYNC_EVENT, sync);
      window.removeEventListener(LEGACY_TRACKED_PLAYER_SYNC_EVENT, sync);
      window.removeEventListener('storage', syncStorage);
    };
  }, []);

  return useMemo(() => ({
    profile,
    setProfile,
    selectPlayer,
    selectBand,
    clearSelectedProfile,
  }), [clearSelectedProfile, profile, selectBand, selectPlayer, setProfile]);
}