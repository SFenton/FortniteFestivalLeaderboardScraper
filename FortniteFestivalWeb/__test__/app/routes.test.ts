import { describe, it, expect } from 'vitest';
import { Routes, RoutePatterns } from '../../src/routes';

describe('Routes', () => {
  it('has songs route', () => {
    expect(Routes.songs).toBe('/songs');
  });

  it('has statistics route', () => {
    expect(Routes.statistics).toBe('/statistics');
  });

  it('has suggestions route', () => {
    expect(Routes.suggestions).toBe('/suggestions');
  });

  it('has settings route', () => {
    expect(Routes.settings).toBe('/settings');
  });

  it('generates song detail path', () => {
    expect(Routes.songDetail('abc-123')).toBe('/songs/abc-123');
  });

  it('generates leaderboard path', () => {
    expect(Routes.leaderboard('abc-123', 'Solo_Guitar')).toBe('/songs/abc-123/Solo_Guitar');
  });

  it('generates player history path', () => {
    expect(Routes.playerHistory('abc-123', 'Solo_Guitar')).toBe('/songs/abc-123/Solo_Guitar/history');
  });

  it('generates player path', () => {
    expect(Routes.player('player-id-1')).toBe('/player/player-id-1');
  });

  it('generates rivals path', () => {
    expect(Routes.rivals('player-id-1')).toBe('/player/player-id-1/rivals');
  });

  it('generates rival detail path', () => {
    expect(Routes.rivalDetail('player-id-1', 'rival-id-2')).toBe('/player/player-id-1/rivals/rival-id-2');
  });

  it('generates rival category path', () => {
    expect(Routes.rivalCategory('player-id-1', 'rival-id-2', 'closest_battles')).toBe(
      '/player/player-id-1/rivals/rival-id-2/closest_battles',
    );
  });
});

describe('RoutePatterns', () => {
  describe('songDetail', () => {
    it('matches /songs/abc-123', () => {
      expect(RoutePatterns.songDetail.test('/songs/abc-123')).toBe(true);
    });

    it('does not match /songs/', () => {
      expect(RoutePatterns.songDetail.test('/songs/')).toBe(false);
    });

    it('does not match /songs/abc/def', () => {
      expect(RoutePatterns.songDetail.test('/songs/abc/def')).toBe(false);
    });
  });

  describe('leaderboard', () => {
    it('matches /songs/abc/Solo_Guitar', () => {
      expect(RoutePatterns.leaderboard.test('/songs/abc/Solo_Guitar')).toBe(true);
    });

    it('does not match /songs/abc', () => {
      expect(RoutePatterns.leaderboard.test('/songs/abc')).toBe(false);
    });

    it('does not match /songs/abc/def/ghi', () => {
      expect(RoutePatterns.leaderboard.test('/songs/abc/def/ghi')).toBe(false);
    });
  });

  describe('history', () => {
    it('matches paths ending with /history', () => {
      expect(RoutePatterns.history.test('/songs/abc/Solo_Guitar/history')).toBe(true);
    });

    it('does not match paths not ending with /history', () => {
      expect(RoutePatterns.history.test('/songs/abc/Solo_Guitar')).toBe(false);
    });
  });

  describe('player', () => {
    it('matches /player/some-id', () => {
      expect(RoutePatterns.player.test('/player/some-id')).toBe(true);
    });

    it('does not match /songs/abc', () => {
      expect(RoutePatterns.player.test('/songs/abc')).toBe(false);
    });

    it('matches /player/ prefix', () => {
      expect(RoutePatterns.player.test('/player/')).toBe(true);
    });
  });

  describe('rivals', () => {
    it('matches /player/id/rivals', () => {
      expect(RoutePatterns.rivals.test('/player/some-id/rivals')).toBe(true);
    });

    it('does not match /player/id/rivals/detail', () => {
      expect(RoutePatterns.rivals.test('/player/some-id/rivals/detail')).toBe(false);
    });

    it('does not match /player/id', () => {
      expect(RoutePatterns.rivals.test('/player/some-id')).toBe(false);
    });
  });

  describe('rivalDetail', () => {
    it('matches /player/id/rivals/rival-id', () => {
      expect(RoutePatterns.rivalDetail.test('/player/some-id/rivals/rival-id')).toBe(true);
    });

    it('does not match /player/id/rivals', () => {
      expect(RoutePatterns.rivalDetail.test('/player/some-id/rivals')).toBe(false);
    });

    it('does not match /player/id/rivals/rival-id/category', () => {
      expect(RoutePatterns.rivalDetail.test('/player/some-id/rivals/rival-id/closest_battles')).toBe(false);
    });
  });

  describe('rivalCategory', () => {
    it('matches /player/id/rivals/rival-id/category', () => {
      expect(RoutePatterns.rivalCategory.test('/player/some-id/rivals/rival-id/closest_battles')).toBe(true);
    });

    it('does not match /player/id/rivals/rival-id', () => {
      expect(RoutePatterns.rivalCategory.test('/player/some-id/rivals/rival-id')).toBe(false);
    });
  });
});
