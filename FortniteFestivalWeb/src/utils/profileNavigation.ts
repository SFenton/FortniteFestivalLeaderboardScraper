import type { TrackedPlayer } from '../hooks/data/useTrackedPlayer';
import type { SelectedProfile } from '../hooks/data/useSelectedProfile';
import { Routes as AppRoutes } from '../routes';

export type ProfileClickDestination = string | 'sidebar' | 'search';

/**
 * Determines the destination action for the selected-profile affordance.
 * Returns the navigation target string, 'sidebar', or 'search'.
 */
export function getProfileClickDestination(
  player: TrackedPlayer | null,
  selectedProfile: SelectedProfile | null,
): ProfileClickDestination {
  if (player) return AppRoutes.statistics;
  if (selectedProfile?.type === 'band') {
    const { bandId, bandType, teamKey } = selectedProfile;
    if (bandId && bandType && teamKey) {
      return AppRoutes.statistics;
    }
    return 'sidebar';
  }
  return 'search';
}

export function getStatisticsNavigationPath(
  player: TrackedPlayer | null,
  selectedProfile: SelectedProfile | null,
): string | null {
  if (player || selectedProfile?.type === 'band') return AppRoutes.statistics;
  return null;
}