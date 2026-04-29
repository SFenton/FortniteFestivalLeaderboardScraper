import type { PlayerBandEntry, PlayerBandType, SongBandLeaderboardEntry } from '@festival/core/api/serverTypes';

export const SONG_BAND_TYPES: PlayerBandType[] = ['Band_Duets', 'Band_Trios', 'Band_Quad'];

export function coerceSongBandType(value: string | undefined): PlayerBandType | null {
  return SONG_BAND_TYPES.includes(value as PlayerBandType) ? (value as PlayerBandType) : null;
}

export function songBandTypeLabel(type: PlayerBandType, t: (key: string) => string): string {
  switch (type) {
    case 'Band_Duets': return t('bandList.groups.duos');
    case 'Band_Trios': return t('bandList.groups.trios');
    case 'Band_Quad': return t('bandList.groups.quads');
  }
}

export function songBandToPlayerBandEntry(entry: SongBandLeaderboardEntry): PlayerBandEntry {
  return {
    bandId: entry.bandId,
    teamKey: entry.teamKey,
    bandType: entry.bandType,
    members: entry.members,
  };
}
