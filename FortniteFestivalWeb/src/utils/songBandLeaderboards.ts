import type { PlayerBandEntry, PlayerBandType, ServerInstrumentKey, SongBandLeaderboardEntry } from '@festival/core/api';
import { resolveBandComboDisplayedMembers } from './bandComboMemberDisplay';
import { BAND_TYPES, coerceBandType } from './bandTypes';

export const SONG_BAND_TYPES: readonly PlayerBandType[] = BAND_TYPES;

export function coerceSongBandType(value: string | undefined): PlayerBandType | null {
  return coerceBandType(value) as PlayerBandType | null;
}

export function songBandToPlayerBandEntry(entry: SongBandLeaderboardEntry, activeFilterInstruments?: readonly ServerInstrumentKey[]): PlayerBandEntry {
  return {
    bandId: entry.bandId,
    teamKey: entry.teamKey,
    bandType: entry.bandType,
    members: resolveBandComboDisplayedMembers(entry.members, activeFilterInstruments, entry.comboId ?? undefined),
  };
}
