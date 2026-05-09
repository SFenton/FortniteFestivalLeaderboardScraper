import type { TrackedPlayer } from '../hooks/data/useTrackedPlayer';
import type { SelectedProfile } from '../hooks/data/useSelectedProfile';
import { Routes as AppRoutes } from '../routes';

export type ProfileClickDestination = string | 'sidebar' | 'modal';

/**
 * Determines the destination action for the selected-profile affordance.
 * Returns the navigation target string, 'sidebar', or 'modal'.
 */
export function getProfileClickDestination(
  player: TrackedPlayer | null,
  selectedProfile: SelectedProfile | null,
): ProfileClickDestination {
  if (player) return AppRoutes.statistics;
  if (selectedProfile?.type === 'band') {
    const { bandId, bandType, teamKey, displayName } = selectedProfile;
    if (bandId && bandType && teamKey) {
      return AppRoutes.band(bandId, { bandType, teamKey, names: displayName });
    }
    return 'sidebar';
  }
  return 'modal';
}

export function getStatisticsNavigationPath(
  player: TrackedPlayer | null,
  selectedProfile: SelectedProfile | null,
): string | null {
  if (player || selectedProfile?.type === 'band') return AppRoutes.statistics;
  return null;
}