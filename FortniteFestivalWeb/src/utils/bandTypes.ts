import type { BandType } from '@festival/core/api/serverTypes';

export const BAND_TYPES: BandType[] = ['Band_Duets', 'Band_Trios', 'Band_Quad'];

export function coerceBandType(value: string | undefined): BandType | null {
  return BAND_TYPES.includes(value as BandType) ? (value as BandType) : null;
}

export function bandTypeLabel(type: BandType, t: (key: string) => string): string {
  switch (type) {
    case 'Band_Duets': return t('bandList.groups.duos');
    case 'Band_Trios': return t('bandList.groups.trios');
    case 'Band_Quad': return t('bandList.groups.quads');
  }
}