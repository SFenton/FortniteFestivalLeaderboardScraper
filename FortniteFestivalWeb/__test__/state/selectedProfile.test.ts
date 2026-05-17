import { beforeEach, describe, expect, it } from 'vitest';
import {
  clearSelectedProfileStorage,
  writeSelectedProfile,
  type SelectedBandProfile,
  type SelectedPlayerProfile,
} from '../../src/state/selectedProfile';
import { defaultSongFilters, defaultSongSettings, loadSongSettings, saveSongSettings } from '../../src/utils/songSettings';

const playerOne: SelectedPlayerProfile = {
  type: 'player',
  accountId: 'p1',
  displayName: 'Player One',
};

const playerTwo: SelectedPlayerProfile = {
  type: 'player',
  accountId: 'p2',
  displayName: 'Player Two',
};

const selectedBand: SelectedBandProfile = {
  type: 'band',
  bandId: 'band-1',
  bandType: 'Band_Trios',
  teamKey: 'p1:p2:p3',
  displayName: 'Player One + Player Two + Player Three',
  members: [],
};

function saveActiveSongFilters() {
  saveSongSettings({
    ...defaultSongSettings(),
    sortMode: 'artist',
    sortAscending: false,
    instrument: 'Solo_Guitar',
    filters: {
      ...defaultSongFilters(),
      hasScores: { Solo_Guitar: true },
      selectedBandHasScore: true,
      shopInShop: true,
    },
  });
}

function expectSongFiltersReset() {
  const loaded = loadSongSettings();
  expect(loaded.instrument).toBeNull();
  expect(loaded.sortMode).toBe('artist');
  expect(loaded.sortAscending).toBe(false);
  expect(loaded.filters).toEqual(defaultSongFilters());
}

beforeEach(() => {
  localStorage.clear();
});

describe('selectedProfile state', () => {
  it('resets song filters when switching from a player to a band', () => {
    writeSelectedProfile(playerOne);
    saveActiveSongFilters();

    writeSelectedProfile(selectedBand);

    expectSongFiltersReset();
  });

  it('resets song filters when switching from a band to a player', () => {
    writeSelectedProfile(selectedBand);
    saveActiveSongFilters();

    writeSelectedProfile(playerOne);

    expectSongFiltersReset();
  });

  it('resets song filters when the selected profile is cleared', () => {
    writeSelectedProfile(selectedBand);
    saveActiveSongFilters();

    clearSelectedProfileStorage();

    expectSongFiltersReset();
  });

  it('keeps song filters when switching between profiles of the same type', () => {
    writeSelectedProfile(playerOne);
    saveActiveSongFilters();

    writeSelectedProfile(playerTwo);

    const loaded = loadSongSettings();
    expect(loaded.instrument).toBe('Solo_Guitar');
    expect(loaded.filters.hasScores).toEqual({ Solo_Guitar: true });
    expect(loaded.filters.shopInShop).toBe(true);
  });
});