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

  it('has rivals route', () => {
    expect(Routes.rivals).toBe('/rivals');
  });

  it('generates all rivals path with category', () => {
    expect(Routes.allRivals('common')).toBe('/rivals/all?category=common');
  });

  it('generates all rivals path with instrument category', () => {
    expect(Routes.allRivals('Solo_Guitar')).toBe('/rivals/all?category=Solo_Guitar');
  });

  it('generates all rivals path with combo category', () => {
    expect(Routes.allRivals('combo')).toBe('/rivals/all?category=combo');
  });

  it('encodes special characters in category', () => {
    expect(Routes.allRivals('Solo_Guitar+Solo_Bass')).toBe(
      '/rivals/all?category=Solo_Guitar%2BSolo_Bass',
    );
  });

  it('generates rival detail path', () => {
    expect(Routes.rivalDetail('rival-id-2')).toBe('/rivals/rival-id-2');
  });

  it('generates rival detail path with name', () => {
    expect(Routes.rivalDetail('rival-id-2', 'TestName')).toBe('/rivals/rival-id-2?name=TestName');
  });

  it('generates rivalry path', () => {
    expect(Routes.rivalry('rival-id-2', 'closest_battles')).toBe(
      '/rivals/rival-id-2/rivalry?mode=closest_battles',
    );
  });

  it('generates full rankings path with rankBy', () => {
    expect(Routes.fullRankings('Solo_Guitar', 'totalscore')).toBe('/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore');
  });

  it('generates full rankings path with rankBy and page', () => {
    expect(Routes.fullRankings('Solo_Guitar', 'totalscore', 2)).toBe('/leaderboards/all?instrument=Solo_Guitar&rankBy=totalscore&page=2');
  });

  it('generates full combo rankings path with rankBy and page', () => {
    expect(Routes.fullComboRankings('05', 'totalscore', 2)).toBe('/leaderboards/all?combo=05&rankBy=totalscore&page=2');
  });

  it('encodes special characters in rivalry mode', () => {
    expect(Routes.rivalry('rival-id-2', 'almost_passed')).toBe(
      '/rivals/rival-id-2/rivalry?mode=almost_passed',
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
    it('matches /rivals', () => {
      expect(RoutePatterns.rivals.test('/rivals')).toBe(true);
    });

    it('does not match /rivals/detail', () => {
      expect(RoutePatterns.rivals.test('/rivals/detail')).toBe(false);
    });
  });

  describe('allRivals', () => {
    it('matches /rivals/all', () => {
      expect(RoutePatterns.allRivals.test('/rivals/all')).toBe(true);
    });

    it('matches /rivals/all?category=common', () => {
      expect(RoutePatterns.allRivals.test('/rivals/all?category=common')).toBe(true);
    });

    it('matches /rivals/all?category=Solo_Guitar', () => {
      expect(RoutePatterns.allRivals.test('/rivals/all?category=Solo_Guitar')).toBe(true);
    });

    it('does not match /rivals', () => {
      expect(RoutePatterns.allRivals.test('/rivals')).toBe(false);
    });
  });

  describe('rivalDetail', () => {
    it('matches /rivals/rival-id', () => {
      expect(RoutePatterns.rivalDetail.test('/rivals/rival-id')).toBe(true);
    });

    it('does not match /rivals', () => {
      expect(RoutePatterns.rivalDetail.test('/rivals')).toBe(false);
    });

    it('does not match /rivals/rival-id/rivalry', () => {
      expect(RoutePatterns.rivalDetail.test('/rivals/rival-id/rivalry')).toBe(false);
    });
  });

  describe('rivalry', () => {
    it('matches /rivals/rival-id/rivalry', () => {
      expect(RoutePatterns.rivalry.test('/rivals/rival-id/rivalry')).toBe(true);
    });

    it('matches /rivals/rival-id/rivalry?mode=closest_battles', () => {
      expect(RoutePatterns.rivalry.test('/rivals/rival-id/rivalry?mode=closest_battles')).toBe(true);
    });

    it('does not match /rivals/rival-id', () => {
      expect(RoutePatterns.rivalry.test('/rivals/rival-id')).toBe(false);
    });
  });
});
