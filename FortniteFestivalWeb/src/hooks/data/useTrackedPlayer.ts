import { useCallback, useMemo } from 'react';
import { useSelectedProfile } from './useSelectedProfile';

export type TrackedPlayer = {
  accountId: string;
  displayName: string;
};

export function useTrackedPlayer() {
  const { profile, selectPlayer, clearSelectedProfile } = useSelectedProfile();

  const player = useMemo(() => profile?.type === 'player'
    ? { accountId: profile.accountId, displayName: profile.displayName }
    : null, [profile]);

  const setPlayer = useCallback((nextPlayer: TrackedPlayer) => {
    selectPlayer(nextPlayer);
  }, [selectPlayer]);

  const clearPlayer = useCallback(() => {
    clearSelectedProfile();
  }, [clearSelectedProfile]);

  return useMemo(() => ({ profile, player, setPlayer, clearPlayer }), [clearPlayer, player, profile, setPlayer]);
}
