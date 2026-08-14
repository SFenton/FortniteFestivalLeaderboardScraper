import { normalizeRoutePathname, RoutePatterns, Routes } from './routes';

export type RouteMetadata = readonly [titleKey: string, fallbackTitle: string];

const META = {
  songs: ['nav.songs', 'Songs'],
  songDetail: ['nav.songInfo', 'Song'],
  songBand: ['rankings.title', 'Band Leaderboard'],
  leaderboard: ['rankings.title', 'Leaderboard'],
  history: ['history.title', 'Score History'],
  player: ['common.playerProfile', 'Player Profile'],
  rivals: ['rivals.title', 'Rivals'],
  allRivals: ['rivals.allTitle', 'All Rivals'],
  rivalDetail: ['rivals.detailTitle', 'Rival Details'],
  rivalry: ['rivals.rivalryTitle', 'Rivalry'],
  statistics: ['nav.statistics', 'Statistics'],
  suggestions: ['nav.suggestions', 'Suggestions'],
  shop: ['nav.shop', 'Item Shop'],
  manual: ['appManual.title', 'Manual'],
  leaderboards: ['rankings.title', 'Leaderboards'],
  fullRankings: ['rankings.title', 'Rankings'],
  bandRankings: ['rankings.title', 'Band Rankings'],
  playerBands: ['bandList.title', 'Player Bands'],
  bands: ['bandList.title', 'Bands'],
  compete: ['compete.title', 'Compete'],
  settings: ['settings.title', 'Settings'],
  licenses: ['settings.licenses.title', 'Licenses'],
  notFound: ['apiError.notFound', 'Not Found'],
} as const satisfies Record<string, RouteMetadata>;

export function matchRouteMetadata(pathname: string): RouteMetadata {
  const path = normalizeRoutePathname(pathname);
  if (RoutePatterns.history.test(path)) return META.history;
  if (RoutePatterns.songBandLeaderboard.test(path)) return META.songBand;
  if (RoutePatterns.leaderboard.test(path)) return META.leaderboard;
  if (RoutePatterns.songDetail.test(path)) return META.songDetail;
  if (path === Routes.settingsLicenses) return META.licenses;
  if (RoutePatterns.rivalry.test(path)) return META.rivalry;
  if (RoutePatterns.allRivals.test(path)) return META.allRivals;
  if (RoutePatterns.rivalDetail.test(path)) return META.rivalDetail;
  if (RoutePatterns.player.test(path)) return META.player;
  if (path === Routes.fullRankingsRoot) return META.fullRankings;
  if (RoutePatterns.bandRankings.test(path)) return META.bandRankings;
  if (RoutePatterns.playerBands.test(path)) return META.playerBands;
  if (RoutePatterns.bands.test(path)) return META.bands;

  switch (path) {
    case Routes.statistics: return META.statistics;
    case Routes.suggestions: return META.suggestions;
    case Routes.shop: return META.shop;
    case Routes.manual: return META.manual;
    case Routes.leaderboards: return META.leaderboards;
    case Routes.compete: return META.compete;
    case Routes.settings: return META.settings;
    case Routes.rivals: return META.rivals;
    case Routes.songs:
    case Routes.root:
      return META.songs;
    default:
      return META.notFound;
  }
}
