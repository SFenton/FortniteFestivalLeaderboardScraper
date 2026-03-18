import { describe, it, expect } from 'vitest';
import {
  cleanFilters,
  buildStarFilter,
  buildPercentileFilter,
  percentileGoldColor,
  PERCENTILE_THRESHOLDS,
} from '../../../../pages/player/helpers/playerFilterHelpers';
import type { SongSettings } from '../../../../utils/songSettings';

function baseSongSettings(): SongSettings {
  return {
    sortMode: 'title',
    sortAscending: true,
    instrument: null,
    metadataOrder: ['score', 'percentage', 'percentile', 'stars', 'intensity', 'seasonachieved'],
    instrumentOrder: ['Solo_Guitar', 'Solo_Bass', 'Solo_Drums', 'Solo_Vocals', 'Solo_PeripheralGuitar', 'Solo_PeripheralBass'],
    filters: {
      seasonFilter: { 5: true },
      percentileFilter: { 10: true },
      starsFilter: { 6: true },
      difficultyFilter: { 3: true },
      missingScores: { Solo_Guitar: true, Solo_Bass: false },
      missingFCs: { Solo_Guitar: false, Solo_Bass: true },
      hasScores: { Solo_Guitar: true, Solo_Bass: false },
      hasFCs: { Solo_Guitar: false, Solo_Bass: true },
    },
  };
}

describe('playerFilterHelpers', () => {
  describe('cleanFilters', () => {
    it('resets instrument-specific filters (season, percentile, stars, difficulty)', () => {
      const s = baseSongSettings();
      const result = cleanFilters(s, 'Solo_Guitar');
      expect(result.seasonFilter).toEqual({});
      expect(result.percentileFilter).toEqual({});
      expect(result.starsFilter).toEqual({});
      expect(result.difficultyFilter).toEqual({});
    });

    it('resets missingScores and hasScores for the given instrument', () => {
      const s = baseSongSettings();
      const result = cleanFilters(s, 'Solo_Guitar');
      expect(result.missingScores.Solo_Guitar).toBe(false);
      expect(result.hasScores.Solo_Guitar).toBe(false);
      expect(result.missingFCs.Solo_Guitar).toBe(false);
      expect(result.hasFCs.Solo_Guitar).toBe(false);
    });

    it('preserves other instruments state', () => {
      const s = baseSongSettings();
      const result = cleanFilters(s, 'Solo_Guitar');
      // Solo_Bass togges should be preserved
      expect(result.missingFCs.Solo_Bass).toBe(true);
      expect(result.hasFCs.Solo_Bass).toBe(true);
    });

    it('works with different instruments', () => {
      const s = baseSongSettings();
      const result = cleanFilters(s, 'Solo_Bass');
      expect(result.missingScores.Solo_Bass).toBe(false);
      expect(result.missingFCs.Solo_Bass).toBe(false);
      expect(result.hasScores.Solo_Bass).toBe(false);
      expect(result.hasFCs.Solo_Bass).toBe(false);
      // Solo_Guitar should be preserved
      expect(result.missingScores.Solo_Guitar).toBe(true);
      expect(result.hasScores.Solo_Guitar).toBe(true);
    });
  });

  describe('buildStarFilter', () => {
    it('enables only the given star key', () => {
      const result = buildStarFilter(6);
      expect(result[6]).toBe(true);
      expect(result[0]).toBe(false);
      expect(result[1]).toBe(false);
      expect(result[2]).toBe(false);
      expect(result[3]).toBe(false);
      expect(result[4]).toBe(false);
      expect(result[5]).toBe(false);
    });

    it('works for star key 0', () => {
      const result = buildStarFilter(0);
      expect(result[0]).toBe(true);
      expect(result[1]).toBe(false);
      expect(result[6]).toBe(false);
    });

    it('works for star key 3', () => {
      const result = buildStarFilter(3);
      expect(result[3]).toBe(true);
      expect(result[6]).toBe(false);
      expect(result[0]).toBe(false);
    });

    it('sets exactly 7 standard keys to false + 1 to true', () => {
      const result = buildStarFilter(5);
      const trueKeys = Object.entries(result).filter(([, v]) => v === true);
      expect(trueKeys.length).toBe(1);
      expect(trueKeys[0]![0]).toBe('5');
    });
  });

  describe('buildPercentileFilter', () => {
    it('enables only the given percentile', () => {
      const result = buildPercentileFilter(5);
      expect(result[5]).toBe(true);
      expect(result[0]).toBe(false);
      for (const t of PERCENTILE_THRESHOLDS) {
        if (t !== 5) expect(result[t]).toBe(false);
      }
    });

    it('handles top 1% bucket', () => {
      const result = buildPercentileFilter(1);
      expect(result[1]).toBe(true);
      expect(result[2]).toBe(false);
      expect(result[100]).toBe(false);
    });

    it('handles 100% bucket', () => {
      const result = buildPercentileFilter(100);
      expect(result[100]).toBe(true);
      expect(result[1]).toBe(false);
    });

    it('has exactly one true value among thresholds', () => {
      const result = buildPercentileFilter(25);
      const trueThresholds = PERCENTILE_THRESHOLDS.filter(t => result[t] === true);
      expect(trueThresholds).toEqual([25]);
    });

    it('always sets 0 to false', () => {
      for (const t of PERCENTILE_THRESHOLDS) {
        const result = buildPercentileFilter(t);
        expect(result[0]).toBe(false);
      }
    });
  });

  describe('percentileGoldColor', () => {
    it('returns undefined for "Top 1%" through "Top 5%"', () => {
      // The function currently always returns undefined, test its behavior
      expect(percentileGoldColor('Top 1%')).toBeUndefined();
      expect(percentileGoldColor('Top 2%')).toBeUndefined();
      expect(percentileGoldColor('Top 3%')).toBeUndefined();
      expect(percentileGoldColor('Top 4%')).toBeUndefined();
      expect(percentileGoldColor('Top 5%')).toBeUndefined();
    });

    it('returns undefined for non-gold percentiles', () => {
      expect(percentileGoldColor('Top 10%')).toBeUndefined();
      expect(percentileGoldColor('Top 50%')).toBeUndefined();
      expect(percentileGoldColor('Top 100%')).toBeUndefined();
    });

    it('returns undefined for non-matching strings', () => {
      expect(percentileGoldColor('')).toBeUndefined();
      expect(percentileGoldColor('Random')).toBeUndefined();
    });
  });

  describe('PERCENTILE_THRESHOLDS', () => {
    it('is sorted ascending', () => {
      for (let i = 1; i < PERCENTILE_THRESHOLDS.length; i++) {
        expect(PERCENTILE_THRESHOLDS[i]).toBeGreaterThan(PERCENTILE_THRESHOLDS[i - 1]!);
      }
    });

    it('starts at 1 and ends at 100', () => {
      expect(PERCENTILE_THRESHOLDS[0]).toBe(1);
      expect(PERCENTILE_THRESHOLDS[PERCENTILE_THRESHOLDS.length - 1]).toBe(100);
    });

    it('has expected length', () => {
      expect(PERCENTILE_THRESHOLDS.length).toBe(17);
    });
  });
});
