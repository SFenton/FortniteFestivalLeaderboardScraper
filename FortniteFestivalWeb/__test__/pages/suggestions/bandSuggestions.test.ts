import { describe, expect, it } from 'vitest';
import type { BandSongPerformance, ServerSong } from '@festival/core/api/serverTypes';
import { buildBandSuggestionSource } from '../../../src/pages/suggestions/bandSuggestions';

const songs: ServerSong[] = [
  { songId: 's1', title: 'Alpha Run', artist: 'The Testers', year: 2023 },
  { songId: 's2', title: 'Beta Push', artist: 'The Testers', year: 2024 },
  { songId: 's3', title: 'Gamma Reset', artist: 'The Testers', year: 2025 },
];

const performances: BandSongPerformance[] = [
  {
    songId: 's1',
    comboId: 'Solo_Guitar+Solo_Bass',
    rank: 42,
    totalEntries: 2000,
    percentile: 2.1,
    score: 995000,
    accuracy: 98.4,
    isFullCombo: false,
    stars: 6,
    season: 4,
  },
  {
    songId: 's2',
    comboId: 'Solo_Guitar+Solo_Bass',
    rank: 84,
    totalEntries: 2000,
    percentile: 4.4,
    score: 860000,
    accuracy: 940000,
    isFullCombo: false,
    stars: 5,
    season: 5,
  },
  {
    songId: 'unknown-song',
    comboId: 'Solo_Guitar+Solo_Bass',
    rank: 1,
    totalEntries: 10,
    percentile: 1,
    score: 123,
    accuracy: 100,
    isFullCombo: true,
    stars: 6,
    season: 5,
  },
];

describe('buildBandSuggestionSource', () => {
  it('converts band rows into core songs and a virtual tracker index', () => {
    const source = buildBandSuggestionSource({
      songs,
      performances,
      bandType: 'Band_Duets',
      comboId: 'Solo_Guitar+Solo_Bass',
      currentSeason: 5,
    });

    expect(source.songs.map(song => song.track.su)).toEqual(['s1', 's2', 's3']);
    expect(Object.keys(source.scoresIndex)).toEqual(['s1', 's2']);
    expect(source.scoresIndex.s1!.guitar).toMatchObject({
      initialized: true,
      maxScore: 995000,
      numStars: 6,
      isFullCombo: false,
      percentHit: 984000,
      rank: 42,
      totalEntries: 2000,
      seasonAchieved: 4,
    });
    expect(source.scoresIndex.s1!.guitar?.rawPercentile).toBeCloseTo(0.021);
    expect(source.scoresIndex.s2!.guitar).toMatchObject({
      initialized: true,
      maxScore: 860000,
      numStars: 5,
      percentHit: 940000,
    });
    expect(source.scoresIndex.s2!.guitar?.rawPercentile).toBeCloseTo(0.044);
  });

  it('normalizes fractional accuracy values to tracker raw accuracy', () => {
    const source = buildBandSuggestionSource({
      songs,
      performances: [{ ...performances[0]!, accuracy: 0.98765 }],
      bandType: 'Band_Duets',
    });

    expect(source.scoresIndex.s1!.guitar?.percentHit).toBe(987650);
  });
});
