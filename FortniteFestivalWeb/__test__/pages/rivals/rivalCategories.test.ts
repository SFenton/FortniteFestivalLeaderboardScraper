import { describe, it, expect } from 'vitest';
import { categorizeRivalSongs } from '../../../src/pages/rivals/helpers/rivalCategories';
import type { RivalSongComparison } from '@festival/core/api/serverTypes';

function makeSong(overrides: Partial<RivalSongComparison> = {}): RivalSongComparison {
  return {
    songId: 'song-1',
    title: 'Test Song',
    artist: 'Test Artist',
    instrument: 'Solo_Guitar',
    userRank: 10,
    rivalRank: 15,
    rankDelta: 5,
    userScore: 95000,
    rivalScore: 90000,
    ...overrides,
  };
}

describe('categorizeRivalSongs', () => {
  it('returns empty array for empty input', () => {
    expect(categorizeRivalSongs([])).toEqual([]);
  });

  it('categorizes single song with positive delta into user-leads categories', () => {
    const songs = [makeSong({ rankDelta: 10 })];
    const cats = categorizeRivalSongs(songs);
    // Should have closest battles + at least one user-leads category
    expect(cats.length).toBeGreaterThanOrEqual(1);
    const keys = cats.map(c => c.key);
    expect(keys).toContain('closest_battles');
    // Single positive delta should go into one of the user-leads buckets
    const userLeadCats = cats.filter(c => c.sentiment === 'positive');
    expect(userLeadCats.length).toBe(1);
  });

  it('categorizes single song with negative delta into rival-leads categories', () => {
    const songs = [makeSong({ rankDelta: -10 })];
    const cats = categorizeRivalSongs(songs);
    expect(cats.length).toBeGreaterThanOrEqual(1);
    const rivalLeadCats = cats.filter(c => c.sentiment === 'negative');
    expect(rivalLeadCats.length).toBe(1);
  });

  it('puts ties (delta=0) only in closest battles', () => {
    const songs = [makeSong({ rankDelta: 0 })];
    const cats = categorizeRivalSongs(songs);
    expect(cats.length).toBe(1);
    expect(cats[0].key).toBe('closest_battles');
    expect(cats[0].sentiment).toBe('neutral');
  });

  it('splits mixed deltas into correct categories', () => {
    const songs = [
      makeSong({ songId: 's1', rankDelta: 50 }),
      makeSong({ songId: 's2', rankDelta: 20 }),
      makeSong({ songId: 's3', rankDelta: 5 }),
      makeSong({ songId: 's4', rankDelta: -3 }),
      makeSong({ songId: 's5', rankDelta: -15 }),
      makeSong({ songId: 's6', rankDelta: -40 }),
    ];
    const cats = categorizeRivalSongs(songs);

    // Should have closest_battles + some user-leads + some rival-leads
    const keys = cats.map(c => c.key);
    expect(keys).toContain('closest_battles');

    const positiveCats = cats.filter(c => c.sentiment === 'positive');
    const negativeCats = cats.filter(c => c.sentiment === 'negative');
    expect(positiveCats.length).toBeGreaterThan(0);
    expect(negativeCats.length).toBeGreaterThan(0);

    // All songs in positive categories should have rankDelta > 0
    for (const cat of positiveCats) {
      for (const s of cat.songs) {
        expect(s.rankDelta).toBeGreaterThan(0);
      }
    }
    // All songs in negative categories should have rankDelta < 0
    for (const cat of negativeCats) {
      for (const s of cat.songs) {
        expect(s.rankDelta).toBeLessThan(0);
      }
    }
  });

  it('closest battles are sorted by absolute delta', () => {
    const songs = [
      makeSong({ songId: 's1', rankDelta: 100 }),
      makeSong({ songId: 's2', rankDelta: -1 }),
      makeSong({ songId: 's3', rankDelta: 2 }),
      makeSong({ songId: 's4', rankDelta: -50 }),
      makeSong({ songId: 's5', rankDelta: 0 }),
    ];
    const cats = categorizeRivalSongs(songs);
    const closest = cats.find(c => c.key === 'closest_battles');
    expect(closest).toBeDefined();
    // First song should have smallest |delta|
    const deltas = closest!.songs.map(s => Math.abs(s.rankDelta));
    for (let i = 1; i < deltas.length; i++) {
      expect(deltas[i]).toBeGreaterThanOrEqual(deltas[i - 1]);
    }
  });

  it('handles all-positive deltas (user dominates)', () => {
    const songs = Array.from({ length: 9 }, (_, i) =>
      makeSong({ songId: `s${i}`, rankDelta: (i + 1) * 10 }),
    );
    const cats = categorizeRivalSongs(songs);
    // Should have barely_winning, pulling_forward, dominating_them + closest_battles
    const keys = cats.map(c => c.key);
    expect(keys).toContain('closest_battles');
    expect(keys).toContain('barely_winning');
    expect(keys).toContain('pulling_forward');
    expect(keys).toContain('dominating_them');

    // No rival-leads categories
    const negativeCats = cats.filter(c => c.sentiment === 'negative');
    expect(negativeCats.length).toBe(0);
  });

  it('handles all-negative deltas (rival dominates)', () => {
    const songs = Array.from({ length: 6 }, (_, i) =>
      makeSong({ songId: `s${i}`, rankDelta: -(i + 1) * 10 }),
    );
    const cats = categorizeRivalSongs(songs);
    const keys = cats.map(c => c.key);
    expect(keys).toContain('closest_battles');
    expect(keys).toContain('almost_passed');
    expect(keys).toContain('slipping_away');

    // No user-leads categories
    const positiveCats = cats.filter(c => c.sentiment === 'positive');
    expect(positiveCats.length).toBe(0);
  });

  it('omits empty categories', () => {
    // 2 songs, both positive — should not produce rival-leads categories
    const songs = [
      makeSong({ songId: 's1', rankDelta: 5 }),
      makeSong({ songId: 's2', rankDelta: 10 }),
    ];
    const cats = categorizeRivalSongs(songs);
    for (const cat of cats) {
      expect(cat.songs.length).toBeGreaterThan(0);
    }
  });

  it('every category has valid i18n keys', () => {
    const songs = Array.from({ length: 12 }, (_, i) =>
      makeSong({ songId: `s${i}`, rankDelta: i - 6 }),
    );
    const cats = categorizeRivalSongs(songs);
    for (const cat of cats) {
      expect(cat.titleKey).toMatch(/^rivals\.detail\./);
      expect(cat.descriptionKey).toMatch(/^rivals\.detail\./);
      expect(['positive', 'negative', 'neutral']).toContain(cat.sentiment);
    }
  });
});
