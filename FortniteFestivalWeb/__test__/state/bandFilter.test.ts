import { beforeEach, describe, expect, it } from 'vitest';
import {
  BAND_FILTER_STORAGE_KEY,
  clearAppliedBandFilter,
  isBandFilterForSelectedProfile,
  normalizeAppliedBandFilter,
  readAppliedBandFilter,
  readAppliedBandFilterForSelectedProfile,
  writeAppliedBandFilter,
} from '../../src/state/bandFilter';
import type { AppliedBandComboFilter } from '../../src/types/bandFilter';
import type { SelectedProfile } from '../../src/state/selectedProfile';

const selectedBand: SelectedProfile = {
  type: 'band',
  bandId: 'band-1',
  bandType: 'Band_Duets',
  teamKey: 'p1:p2',
  displayName: 'Band One',
  members: [],
};

const selectedPlayer: SelectedProfile = {
  type: 'player',
  accountId: 'p1',
  displayName: 'Player One',
};

const filter: AppliedBandComboFilter = {
  bandId: 'band-1',
  bandType: 'Band_Duets',
  teamKey: 'p1:p2',
  comboId: 'Solo_Guitar+Solo_Bass',
  assignments: [
    { accountId: 'p1', instrument: 'Solo_Guitar' },
    { accountId: 'p2', instrument: 'Solo_Bass' },
  ],
  configurations: [{
    rawInstrumentCombo: 'Solo_Guitar+Solo_Bass',
    comboId: 'Solo_Guitar+Solo_Bass',
    instruments: ['Solo_Guitar', 'Solo_Bass'],
    assignmentKey: 'p1=Solo_Guitar;p2=Solo_Bass',
    appearanceCount: 4,
    memberInstruments: {
      p1: 'Solo_Guitar',
      p2: 'Solo_Bass',
    },
  }],
};

beforeEach(() => {
  localStorage.clear();
});

describe('bandFilter state', () => {
  it('normalizes a valid applied band filter', () => {
    expect(normalizeAppliedBandFilter(filter)).toEqual(filter);
  });

  it('rejects missing identity fields and invalid instruments', () => {
    expect(normalizeAppliedBandFilter({ ...filter, bandId: '' })).toBeNull();
    expect(normalizeAppliedBandFilter({ ...filter, assignments: [{ accountId: 'p1', instrument: 'Bad_Instrument' }] })).toBeNull();
  });

  it('roundtrips applied filters through localStorage', () => {
    writeAppliedBandFilter(filter);

    expect(JSON.parse(localStorage.getItem(BAND_FILTER_STORAGE_KEY)!)).toEqual(filter);
    expect(readAppliedBandFilter()).toEqual(filter);
  });

  it('clears corrupted storage on read', () => {
    localStorage.setItem(BAND_FILTER_STORAGE_KEY, 'not-json');

    expect(readAppliedBandFilter()).toBeNull();
    expect(localStorage.getItem(BAND_FILTER_STORAGE_KEY)).toBeNull();
  });

  it('keeps a stored filter for the same selected band identity', () => {
    writeAppliedBandFilter(filter);

    expect(readAppliedBandFilterForSelectedProfile(selectedBand)).toEqual(filter);
    expect(localStorage.getItem(BAND_FILTER_STORAGE_KEY)).toBeTruthy();
  });

  it('clears a stored filter when the selected band changes', () => {
    writeAppliedBandFilter(filter);
    const nextBand: SelectedProfile = { ...selectedBand, bandId: 'band-2' };

    expect(readAppliedBandFilterForSelectedProfile(nextBand)).toBeNull();
    expect(localStorage.getItem(BAND_FILTER_STORAGE_KEY)).toBeNull();
  });

  it('clears a stored filter when the selected team key changes', () => {
    writeAppliedBandFilter(filter);
    const nextBand: SelectedProfile = { ...selectedBand, teamKey: 'p1:p3' };

    expect(readAppliedBandFilterForSelectedProfile(nextBand)).toBeNull();
    expect(localStorage.getItem(BAND_FILTER_STORAGE_KEY)).toBeNull();
  });

  it('clears a stored filter for deselect and player selection', () => {
    writeAppliedBandFilter(filter);
    expect(readAppliedBandFilterForSelectedProfile(null)).toBeNull();
    expect(localStorage.getItem(BAND_FILTER_STORAGE_KEY)).toBeNull();

    writeAppliedBandFilter(filter);
    expect(readAppliedBandFilterForSelectedProfile(selectedPlayer)).toBeNull();
    expect(localStorage.getItem(BAND_FILTER_STORAGE_KEY)).toBeNull();
  });

  it('matches filters only to the same selected band identity', () => {
    expect(isBandFilterForSelectedProfile(filter, selectedBand)).toBe(true);
    expect(isBandFilterForSelectedProfile(filter, { ...selectedBand, bandType: 'Band_Trios' })).toBe(false);
    expect(isBandFilterForSelectedProfile(filter, selectedPlayer)).toBe(false);
    expect(isBandFilterForSelectedProfile(null, selectedBand)).toBe(false);
  });

  it('removes the persisted filter on explicit clear', () => {
    writeAppliedBandFilter(filter);

    clearAppliedBandFilter();

    expect(localStorage.getItem(BAND_FILTER_STORAGE_KEY)).toBeNull();
  });
});