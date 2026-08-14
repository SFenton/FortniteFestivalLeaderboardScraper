import { describe, expect, it, vi } from 'vitest';
import { BAND_TYPES, bandTypeLabel, coerceBandType } from '../../src/utils/bandTypes';
import { SONG_BAND_TYPES, coerceSongBandType } from '../../src/utils/songBandLeaderboards';

describe('band type ownership', () => {
  it('shares one ordered taxonomy across band API aliases', () => {
    expect(SONG_BAND_TYPES).toBe(BAND_TYPES);
    expect(BAND_TYPES).toEqual([
      'Band_Duets',
      'Band_Trios',
      'Band_Quad',
    ]);
  });

  it('coerces valid values through the shared taxonomy', () => {
    expect(coerceBandType('Band_Duets')).toBe('Band_Duets');
    expect(coerceSongBandType('Band_Quad')).toBe('Band_Quad');
    expect(coerceBandType('Band_Unknown')).toBeNull();
    expect(coerceSongBandType(undefined)).toBeNull();
  });

  it('resolves localized labels at render time', () => {
    const t = vi.fn((key: string) => `translated:${key}`);

    expect(bandTypeLabel('Band_Duets', t)).toBe(
      'translated:bandList.groups.duos',
    );
    expect(bandTypeLabel('Band_Trios', t)).toBe(
      'translated:bandList.groups.trios',
    );
    expect(bandTypeLabel('Band_Quad', t)).toBe(
      'translated:bandList.groups.quads',
    );
  });
});
