import { describe, expect, it } from 'vitest';
import type { SelectedProfile } from '../../src/hooks/data/useSelectedProfile';
import {
  getBandLookupProfileRoute,
  getBandProfileRoute,
  getPlayerProfileRoute,
  isSelectedBandRoute,
} from '../../src/utils/profileNavigation';

const selectedPlayer: SelectedProfile = {
  type: 'player',
  accountId: 'player-1',
  displayName: 'Player One',
};

const selectedBand: SelectedProfile = {
  type: 'band',
  bandId: 'band-1',
  bandType: 'Band_Duets',
  teamKey: 'p1:p2',
  displayName: 'Duo One',
  members: [
    { accountId: 'p1', displayName: 'One' },
    { accountId: 'p2', displayName: 'Two' },
  ],
};

describe('profileNavigation selected-profile routes', () => {
  it('routes matching selected player links to statistics', () => {
    expect(getPlayerProfileRoute('player-1', selectedPlayer)).toBe('/statistics');
    expect(getPlayerProfileRoute('player-2', selectedPlayer)).toBe('/player/player-2');
  });

  it('routes matching selected band links to statistics by band id or team identity', () => {
    expect(getBandProfileRoute('band-1', { bandType: 'Band_Duets', teamKey: 'p1:p2' }, selectedBand)).toBe('/statistics');
    expect(getBandProfileRoute('band-other', { bandType: 'Band_Duets', teamKey: 'p1:p2' }, selectedBand)).toBe('/statistics');
    expect(getBandLookupProfileRoute('p1', 'Band_Duets', 'p1:p2', 'One + Two', selectedBand)).toBe('/statistics');
  });

  it('keeps non-selected band links on detail routes', () => {
    expect(isSelectedBandRoute('band-other', { bandType: 'Band_Trios', teamKey: 'p1:p2:p3' }, selectedBand)).toBe(false);
    expect(getBandProfileRoute('band-other', { bandType: 'Band_Trios', teamKey: 'p1:p2:p3' }, selectedBand))
      .toBe('/bands/band-other?bandType=Band_Trios&teamKey=p1%3Ap2%3Ap3');
  });
});