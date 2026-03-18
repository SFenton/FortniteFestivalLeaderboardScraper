import { describe, it, expect } from 'vitest';
import { serverSongToCore, buildScoresIndex } from '../../src/utils/suggestionAdapter';
import type { ServerSong, PlayerScore } from '@festival/core/api/serverTypes';

const mockSong: ServerSong = {
  songId: 's1',
  title: 'Test Song',
  artist: 'Test Artist',
  year: 2024,
  albumArt: 'http://img.png',
  tempo: 120,
  difficulty: {
    guitar: 3,
    bass: 2,
    drums: 4,
    vocals: 1,
    proGuitar: 5,
    proBass: 3,
  },
  maxScores: null,
} as unknown as ServerSong;

describe('serverSongToCore', () => {
  it('converts a server song to core format', () => {
    const core = serverSongToCore(mockSong);
    expect(core._title).toBe('Test Song');
    expect(core.track.su).toBe('s1');
    expect(core.track.tt).toBe('Test Song');
    expect(core.track.an).toBe('Test Artist');
    expect(core.track.ry).toBe(2024);
  });

  it('handles song without difficulty', () => {
    const songNoDiff = { ...mockSong, difficulty: undefined };
    const core = serverSongToCore(songNoDiff as any);
    expect(core.track.in).toBeUndefined();
  });

  it('maps difficulty fields correctly', () => {
    const core = serverSongToCore(mockSong);
    expect(core.track.in?.gr).toBe(3);
    expect(core.track.in?.ds).toBe(4);
  });
});

describe('buildScoresIndex', () => {
  it('returns empty object for empty scores', () => {
    expect(buildScoresIndex([])).toEqual({});
  });

  it('builds index from player scores', () => {
    const scores: PlayerScore[] = [
      {
        songId: 's1',
        songTitle: 'Song 1',
        songArtist: 'Artist 1',
        instrument: 'Solo_Guitar' as string,
        score: 50000,
        rank: 5,
        totalEntries: 100,
        isFullCombo: false,
        stars: 4,
        accuracy: 950000,
        season: 2,
        percentile: 5,
      } as PlayerScore,
    ];
    const index = buildScoresIndex(scores);
    expect(index['s1']!).toBeDefined();
    expect(index['s1']!.songId).toBe('s1');
    expect((index['s1']! as any).guitar).toBeDefined();
    expect((index['s1']! as any).guitar.maxScore).toBe(50000);
  });

  it('groups multiple instruments under same song', () => {
    const scores: PlayerScore[] = [
      { songId: 's1', songTitle: 'S', songArtist: 'A', instrument: 'Solo_Guitar', score: 1000, rank: 1, totalEntries: 10, isFullCombo: false, stars: 3, accuracy: 800000, season: 1 } as PlayerScore,
      { songId: 's1', songTitle: 'S', songArtist: 'A', instrument: 'Solo_Bass', score: 2000, rank: 2, totalEntries: 10, isFullCombo: true, stars: 5, accuracy: 1000000, season: 1 } as PlayerScore,
    ];
    const index = buildScoresIndex(scores);
    expect((index['s1']! as any).guitar).toBeDefined();
    expect((index['s1']! as any).bass).toBeDefined();
  });

  it('computes rawPercentile from explicit percentile', () => {
    const scores: PlayerScore[] = [
      { songId: 's1', songTitle: 'S', songArtist: 'A', instrument: 'Solo_Guitar', score: 1000, rank: 1, totalEntries: 100, isFullCombo: false, stars: 3, accuracy: 800000, season: 1, percentile: 5 } as PlayerScore,
    ];
    const index = buildScoresIndex(scores);
    expect((index['s1']! as any).guitar.rawPercentile).toBeCloseTo(0.05);
  });

  it('computes rawPercentile from rank when percentile absent', () => {
    const scores: PlayerScore[] = [
      { songId: 's1', songTitle: 'S', songArtist: 'A', instrument: 'Solo_Guitar', score: 1000, rank: 10, totalEntries: 100, isFullCombo: false, stars: 3, accuracy: 800000, season: 1 } as PlayerScore,
    ];
    const index = buildScoresIndex(scores);
    expect((index['s1']! as any).guitar.rawPercentile).toBeCloseTo(0.1);
  });

  it('skips unknown instrument keys', () => {
    const scores: PlayerScore[] = [
      { songId: 's1', songTitle: 'S', songArtist: 'A', instrument: 'unknown_inst', score: 1000, rank: 1, totalEntries: 10 } as unknown as PlayerScore,
    ];
    const index = buildScoresIndex(scores);
    expect(index['s1']!).toBeUndefined();
  });
});
