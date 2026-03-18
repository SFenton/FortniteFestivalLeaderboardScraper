/**
 * Tests for extracted settings-updater functions from player page sections.
 * These were previously inline onClick lambdas wrapped in v8 ignore.
 */
import { describe, it, expect } from 'vitest';
import { songsPlayedUpdater, fullCombosUpdater } from '../../pages/player/sections/OverallSummarySection';
import {
  instSongsPlayedUpdater,
  instFCsUpdater,
  instStarsUpdater,
  instPercentileUpdater,
  instPercentileWithScoresUpdater,
  instPercentileBucketUpdater,
  pctGold,
} from '../../pages/player/sections/InstrumentStatsSection';
import { defaultSongFilters, type SongSettings } from '../../utils/songSettings';

function baseSongSettings(): SongSettings {
  return {
    instrument: null,
    sortMode: 'title',
    sortAscending: true,
    search: '',
    filters: defaultSongFilters(),
    metadataOrder: [],
    songRowVisualOrder: [],
  } as unknown as SongSettings;
}

describe('OverallSummarySection updaters', () => {
  it('songsPlayedUpdater sets hasScores for all visible keys', () => {
    const updater = songsPlayedUpdater(['Solo_Guitar', 'Solo_Bass']);
    const result = updater(baseSongSettings());
    expect(result.instrument).toBeNull();
    expect(result.sortMode).toBe('title');
    expect(result.sortAscending).toBe(true);
    expect(result.filters.hasScores).toEqual({ Solo_Guitar: true, Solo_Bass: true });
  });

  it('fullCombosUpdater sets hasFCs for all visible keys', () => {
    const updater = fullCombosUpdater(['Solo_Drums', 'Solo_Vocals']);
    const result = updater(baseSongSettings());
    expect(result.instrument).toBeNull();
    expect(result.sortMode).toBe('title');
    expect(result.filters.hasFCs).toEqual({ Solo_Drums: true, Solo_Vocals: true });
  });
});

describe('InstrumentStatsSection updaters', () => {
  it('instSongsPlayedUpdater sets instrument and hasScores filter', () => {
    const updater = instSongsPlayedUpdater('Solo_Guitar');
    const result = updater(baseSongSettings());
    expect(result.instrument).toBe('Solo_Guitar');
    expect(result.sortMode).toBe('score');
    expect(result.filters.hasScores.Solo_Guitar).toBe(true);
  });

  it('instFCsUpdater sets instrument and hasFCs filter', () => {
    const updater = instFCsUpdater('Solo_Bass');
    const result = updater(baseSongSettings());
    expect(result.instrument).toBe('Solo_Bass');
    expect(result.sortMode).toBe('score');
    expect(result.filters.hasFCs.Solo_Bass).toBe(true);
  });

  it('instStarsUpdater sets star filter for given key', () => {
    const updater = instStarsUpdater('Solo_Drums', 6);
    const result = updater(baseSongSettings());
    expect(result.instrument).toBe('Solo_Drums');
    expect(result.sortMode).toBe('stars');
    expect(result.filters.starsFilter).toBeDefined();
  });

  it('instPercentileUpdater sets percentile sort', () => {
    const updater = instPercentileUpdater('Solo_Guitar');
    const result = updater(baseSongSettings());
    expect(result.instrument).toBe('Solo_Guitar');
    expect(result.sortMode).toBe('percentile');
  });

  it('instPercentileWithScoresUpdater sets percentile sort with hasScores', () => {
    const updater = instPercentileWithScoresUpdater('Solo_Guitar');
    const result = updater(baseSongSettings());
    expect(result.instrument).toBe('Solo_Guitar');
    expect(result.sortMode).toBe('percentile');
    expect(result.filters.hasScores.Solo_Guitar).toBe(true);
  });

  it('instPercentileBucketUpdater sets percentile bucket filter', () => {
    const updater = instPercentileBucketUpdater('Solo_Guitar', 5);
    const result = updater(baseSongSettings());
    expect(result.instrument).toBe('Solo_Guitar');
    expect(result.filters.percentileFilter).toBeDefined();
  });

  it('pctGold returns gold for Top 1-5%', () => {
    expect(pctGold('Top 1%')).toBeDefined();
    expect(pctGold('Top 5%')).toBeDefined();
    expect(pctGold('Top 10%')).toBeUndefined();
    expect(pctGold('Bottom 50%')).toBeUndefined();
  });
});
