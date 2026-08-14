import { describe, it, expect } from 'vitest';
import {
  isKnownRoutePath,
  normalizeRoutePathname,
  Routes,
  RoutePatterns,
} from '../../src/routes';
import { matchRouteMetadata } from '../../src/routeMetadata';

describe('Routes', () => {
  it('has songs route', () => {
    expect(Routes.songs).toBe('/songs');
  });

  it('has root and shared route roots', () => {
    expect(Routes.root).toBe('/');
    expect(Routes.allRivalsRoot).toBe('/rivals/all');
    expect(Routes.fullRankingsRoot).toBe('/leaderboards/all');
    expect(Routes.bands).toBe('/bands');
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

  it('has manual route', () => {
    expect(Routes.manual).toBe('/manual');
  });

  it('generates song detail path', () => {
    expect(Routes.songDetail('abc-123')).toBe('/songs/abc-123');
  });

  it('generates leaderboard path', () => {
    expect(Routes.leaderboard('abc-123', 'Solo_Guitar')).toBe('/songs/abc-123/Solo_Guitar');
  });

  it('generates song band leaderboard path', () => {
    expect(Routes.songBandLeaderboard('abc-123', 'Band_Duets')).toBe('/songs/abc-123/bands/Band_Duets');
  });

  it('generates song band leaderboard path with page', () => {
    expect(Routes.songBandLeaderboard('abc-123', 'Band_Quad', 3)).toBe('/songs/abc-123/bands/Band_Quad?page=3');
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

  it('generates band rankings path with rankBy and page', () => {
    expect(Routes.bandRankings('Band_Duets', 'totalscore', 2)).toBe('/leaderboards/bands/Band_Duets?rankBy=totalscore&page=2');
  });

  it('generates player bands path with group, page, and friendly name', () => {
    expect(Routes.playerBands('player id', 'duos', 2, 'Player One')).toBe(
      '/bands/player/player%20id?group=duos&page=2&name=Player%20One',
    );
  });

  it('generates band path without context', () => {
    expect(Routes.band('band-id-1')).toBe('/bands/band-id-1');
  });

  it('generates band path with lookup context', () => {
    expect(Routes.band('band-id-1', { accountId: 'p1', bandType: 'Band_Duets', teamKey: 'p1:p2' })).toBe(
      '/bands/band-id-1?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2',
    );
  });

  it('generates band path with friendly names', () => {
    expect(Routes.band('band id', { names: 'Player One + Player Two' })).toBe(
      '/bands/band%20id?names=Player%20One%20%2B%20Player%20Two',
    );
  });

  it('generates band path with lookup context and friendly names', () => {
    expect(Routes.band('band-id-1', {
      accountId: 'p1',
      bandType: 'Band_Duets',
      teamKey: 'p1:p2',
      names: 'Player One + Player Two',
    })).toBe('/bands/band-id-1?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2&names=Player%20One%20%2B%20Player%20Two');
  });

  it('generates lookup band path with friendly names', () => {
    expect(Routes.bandLookup('p1', 'Band_Duets', 'p1:p2', 'Player One + Player Two')).toBe(
      '/bands?accountId=p1&bandType=Band_Duets&teamKey=p1%3Ap2&names=Player%20One%20%2B%20Player%20Two',
    );
  });

  it('encodes special characters in rivalry mode', () => {
    expect(Routes.rivalry('rival-id-2', 'almost_passed')).toBe(
      '/rivals/rival-id-2/rivalry?mode=almost_passed',
    );
  });
});

describe('RoutePatterns', () => {
  it('classifies rendered paths without accepting malformed descendants', () => {
    expect(isKnownRoutePath('/songs/song-1/Solo_Guitar/history')).toBe(true);
    expect(isKnownRoutePath('/leaderboards/bands/Band_Duets')).toBe(true);
    expect(isKnownRoutePath('/settings/')).toBe(true);
    expect(isKnownRoutePath('/missing/deep-link')).toBe(false);
    expect(isKnownRoutePath('/bands/player/account/extra')).toBe(false);
  });

  it('normalizes trailing slashes without changing the root', () => {
    expect(normalizeRoutePathname('/')).toBe('/');
    expect(normalizeRoutePathname('/settings/')).toBe('/settings');
    expect(normalizeRoutePathname('/songs/song-1///')).toBe('/songs/song-1');
  });

  describe('songDetail', () => {
    it('matches /songs/abc-123', () => {
      expect(RoutePatterns.songDetail.test('/songs/abc-123')).toBe(true);
    });

    describe('matchRouteMetadata', () => {
      it.each([
        ['/songs', 'songs'],
        ['/songs/song-1', 'song-detail'],
        ['/songs/song-1/bands/Band_Duets', 'song-band-leaderboard'],
        ['/songs/song-1/Solo_Guitar', 'leaderboard'],
        ['/songs/song-1/Solo_Guitar/history', 'history'],
        ['/rivals/all', 'all-rivals'],
        ['/rivals/rival-1/rivalry', 'rivalry'],
        ['/rivals/rival-1', 'rival-detail'],
        ['/leaderboards/all', 'full-rankings'],
        ['/leaderboards/bands/Band_Duets', 'band-rankings'],
        ['/bands/player/player-1', 'player-bands'],
        ['/settings/licenses', 'licenses'],
      ])('matches %s before broader route patterns', (pathname, expectedTitleKey) => {
        const expectedKeys: Record<string, string> = {
          songs: 'nav.songs',
          'song-detail': 'nav.songInfo',
          'song-band-leaderboard': 'rankings.title',
          leaderboard: 'rankings.title',
          history: 'history.title',
          'all-rivals': 'rivals.allTitle',
          rivalry: 'rivals.rivalryTitle',
          'rival-detail': 'rivals.detailTitle',
          'full-rankings': 'rankings.title',
          'band-rankings': 'rankings.title',
          'player-bands': 'bandList.title',
          licenses: 'settings.licenses.title',
        };
        expect(matchRouteMetadata(pathname)[0]).toBe(expectedKeys[expectedTitleKey]);
      });

      it('uses Songs metadata for the root redirect and Not Found for unknown paths', () => {
        expect(matchRouteMetadata('/')[0]).toBe('nav.songs');
        expect(matchRouteMetadata('/not-found')[0]).toBe('apiError.notFound');
      });

      it('normalizes trailing slashes before matching metadata', () => {
        expect(matchRouteMetadata('/settings/')[0]).toBe('settings.title');
        expect(matchRouteMetadata('/leaderboards/bands/Band_Duets/')[1]).toBe('Band Rankings');
      });

      it('distinguishes band detail metadata from player band lists', () => {
        expect(matchRouteMetadata('/bands/player/player-1')[1]).toBe('Player Bands');
        expect(matchRouteMetadata('/bands/band-1')[1]).toBe('Bands');
      });
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

  describe('songBandLeaderboard', () => {
    it('matches /songs/abc/bands/Band_Duets', () => {
      expect(RoutePatterns.songBandLeaderboard.test('/songs/abc/bands/Band_Duets')).toBe(true);
    });

    it('does not match solo leaderboard routes', () => {
      expect(RoutePatterns.songBandLeaderboard.test('/songs/abc/Solo_Guitar')).toBe(false);
    });
  });

  describe('history', () => {
    it('matches paths ending with /history', () => {
      expect(RoutePatterns.history.test('/songs/abc/Solo_Guitar/history')).toBe(true);
    });

    it('does not match paths not ending with /history', () => {
      expect(RoutePatterns.history.test('/songs/abc/Solo_Guitar')).toBe(false);
    });

    it('does not match unrelated history suffixes', () => {
      expect(RoutePatterns.history.test('/settings/history')).toBe(false);
    });
  });

  describe('player', () => {
    it('matches /player/some-id', () => {
      expect(RoutePatterns.player.test('/player/some-id')).toBe(true);
    });

    it('does not match /songs/abc', () => {
      expect(RoutePatterns.player.test('/songs/abc')).toBe(false);
    });

    it('requires an account id and no extra segments', () => {
      expect(RoutePatterns.player.test('/player/')).toBe(false);
      expect(RoutePatterns.player.test('/player/account/extra')).toBe(false);
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

    it('does not classify malformed descendants as all-rivals', () => {
      expect(RoutePatterns.allRivals.test('/rivals/all/extra')).toBe(false);
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

    it('matches pathnames rather than path-and-query strings', () => {
      expect(RoutePatterns.rivalry.test('/rivals/rival-id/rivalry?mode=closest_battles')).toBe(false);
    });

    it('does not match /rivals/rival-id', () => {
      expect(RoutePatterns.rivalry.test('/rivals/rival-id')).toBe(false);
    });

    it('does not match rivalry descendants', () => {
      expect(RoutePatterns.rivalry.test('/rivals/rival-id/rivalry/extra')).toBe(false);
    });
  });

  describe('playerBands', () => {
    it('matches account-scoped player bands routes', () => {
      expect(RoutePatterns.playerBands.test('/bands/player/account-id')).toBe(true);
    });

    it('does not match band detail routes', () => {
      expect(RoutePatterns.playerBands.test('/bands/band-id')).toBe(false);
    });

    it('requires an account id and no extra segments', () => {
      expect(RoutePatterns.playerBands.test('/bands/player/')).toBe(false);
      expect(RoutePatterns.playerBands.test('/bands/player/account-id/extra')).toBe(false);
    });
  });
});
