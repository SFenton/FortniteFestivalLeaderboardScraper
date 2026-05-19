import type { TrackedPlayer } from '../hooks/data/useTrackedPlayer';
import type { SelectedProfile } from '../hooks/data/useSelectedProfile';
import { Routes as AppRoutes } from '../routes';

export type BandProfileRouteContext = {
  accountId?: string;
  bandType?: string;
  teamKey?: string;
  names?: string;
};

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

export function isSelectedPlayerAccount(
  accountId: string | null | undefined,
  selectedProfile: SelectedProfile | null | undefined,
): boolean {
  return !!accountId
    && selectedProfile?.type === 'player'
    && selectedProfile.accountId === accountId;
}

export function getPlayerProfileRoute(
  accountId: string,
  selectedProfile: SelectedProfile | null | undefined,
): string {
  return isSelectedPlayerAccount(accountId, selectedProfile)
    ? AppRoutes.statistics
    : AppRoutes.player(accountId);
}

export function isSelectedBandRoute(
  bandId: string | null | undefined,
  context: BandProfileRouteContext | null | undefined,
  selectedProfile: SelectedProfile | null | undefined,
): boolean {
  if (selectedProfile?.type !== 'band') return false;
  const hasBandIdMatch = !!bandId && selectedProfile.bandId === bandId;
  const hasTeamMatch = !!context?.bandType
    && !!context.teamKey
    && selectedProfile.bandType === context.bandType
    && selectedProfile.teamKey === context.teamKey;
  return hasBandIdMatch || hasTeamMatch;
}

export function getBandProfileRoute(
  bandId: string,
  context: BandProfileRouteContext | undefined,
  selectedProfile: SelectedProfile | null | undefined,
): string {
  return isSelectedBandRoute(bandId, context, selectedProfile)
    ? AppRoutes.statistics
    : AppRoutes.band(bandId, context);
}

export function getBandLookupProfileRoute(
  accountId: string,
  bandType: string,
  teamKey: string,
  names: string | undefined,
  selectedProfile: SelectedProfile | null | undefined,
): string {
  const context = { accountId, bandType, teamKey, names };
  return isSelectedBandRoute(null, context, selectedProfile)
    ? AppRoutes.statistics
    : AppRoutes.bandLookup(accountId, bandType, teamKey, names);
}