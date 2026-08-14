/** Centralised route path constants. */
export const Routes = {
  root: '/',
  songs: '/songs',
  songDetail: (songId: string) => `/songs/${songId}`,
  leaderboard: (songId: string, instrument: string) => `/songs/${songId}/${instrument}`,
  songBandLeaderboard: (songId: string, bandType: string, page?: number) =>
    `/songs/${encodeURIComponent(songId)}/bands/${encodeURIComponent(bandType)}${page != null ? `?page=${page}` : ''}`,
  playerHistory: (songId: string, instrument: string) => `/songs/${songId}/${instrument}/history`,
  player: (accountId: string) => `/player/${accountId}`,
  rivals: '/rivals',
  allRivalsRoot: '/rivals/all',
  allRivals: (category: string, mode?: 'leaderboard', rankBy?: string) =>
    `${Routes.allRivalsRoot}?category=${encodeURIComponent(category)}${mode ? `&mode=${encodeURIComponent(mode)}` : ''}${rankBy ? `&rankBy=${encodeURIComponent(rankBy)}` : ''}`,
  rivalDetail: (rivalId: string, rivalName?: string) =>
    `/rivals/${rivalId}${rivalName ? `?name=${encodeURIComponent(rivalName)}` : ''}`,
  rivalry: (rivalId: string, mode: string, name?: string) =>
    `/rivals/${rivalId}/rivalry?mode=${encodeURIComponent(mode)}${name ? `&name=${encodeURIComponent(name)}` : ''}`,
  statistics: '/statistics',
  suggestions: '/suggestions',
  compete: '/compete',
  leaderboards: '/leaderboards',
  fullRankingsRoot: '/leaderboards/all',
  fullRankings: (instrument: string, rankBy?: string, page?: number) =>
    `${Routes.fullRankingsRoot}?instrument=${encodeURIComponent(instrument)}${rankBy ? `&rankBy=${encodeURIComponent(rankBy)}` : ''}${page != null ? `&page=${page}` : ''}`,
  familyRankings: (scopeId: string, rankBy?: string, page?: number) =>
    `${Routes.fullRankingsRoot}?family=${encodeURIComponent(scopeId)}${rankBy ? `&rankBy=${encodeURIComponent(rankBy)}` : ''}${page != null ? `&page=${page}` : ''}`,
  fullComboRankings: (comboId: string, rankBy?: string, page?: number) =>
    `${Routes.fullRankingsRoot}?combo=${encodeURIComponent(comboId)}${rankBy ? `&rankBy=${encodeURIComponent(rankBy)}` : ''}${page != null ? `&page=${page}` : ''}`,
  bandRankings: (bandType: string, rankBy?: string, page?: number) =>
    `/leaderboards/bands/${encodeURIComponent(bandType)}${rankBy ? `?rankBy=${encodeURIComponent(rankBy)}` : ''}${page != null ? `${rankBy ? '&' : '?'}page=${page}` : ''}`,
  playerBands: (accountId: string, group = 'all', page?: number, name?: string) => {
    const params: string[] = [`group=${encodeURIComponent(group)}`];
    if (page != null) params.push(`page=${page}`);
    if (name) params.push(`name=${encodeURIComponent(name)}`);
    return `/bands/player/${encodeURIComponent(accountId)}?${params.join('&')}`;
  },
  bands: '/bands',
  band: (bandId: string, context?: { accountId?: string; bandType?: string; teamKey?: string; names?: string }) => {
    const path = `/bands/${encodeURIComponent(bandId)}`;
    const query = buildBandQuery(context);
    return query ? `${path}?${query}` : path;
  },
  bandLookup: (accountId: string, bandType: string, teamKey: string, names?: string) => {
    const query = buildBandQuery({ accountId, bandType, teamKey, names });
    return `/bands${query ? `?${query}` : ''}`;
  },
  shop: '/shop',
  manual: '/manual',
  settings: '/settings',
  settingsLicenses: '/settings/licenses',
} as const;

function buildBandQuery(context?: { accountId?: string; bandType?: string; teamKey?: string; names?: string }): string {
  if (!context) return '';
  const params: string[] = [];
  if (context.accountId) params.push(`accountId=${encodeURIComponent(context.accountId)}`);
  if (context.bandType) params.push(`bandType=${encodeURIComponent(context.bandType)}`);
  if (context.teamKey) params.push(`teamKey=${encodeURIComponent(context.teamKey)}`);
  if (context.names) params.push(`names=${encodeURIComponent(context.names)}`);
  return params.join('&');
}

/** Regex patterns for route matching. */
export const RoutePatterns = {
  songDetail: /^\/songs\/[^/]+$/,
  songBandLeaderboard: /^\/songs\/[^/]+\/bands\/[^/]+$/,
  leaderboard: /^\/songs\/[^/]+\/[^/]+$/,
  history: /^\/songs\/[^/]+\/[^/]+\/history$/,
  player: /^\/player\/[^/]+$/,
  rivals: /^\/rivals$/,
  allRivals: /^\/rivals\/all$/,
  rivalDetail: /^\/rivals\/[^/]+$/,
  rivalry: /^\/rivals\/[^/]+\/rivalry$/,
  leaderboards: /^\/leaderboards(?:\/all|\/bands\/[^/]+)?$/,
  bandRankings: /^\/leaderboards\/bands\/[^/]+$/,
  manual: /^\/manual$/,
  playerBands: /^\/bands\/player\/[^/]+$/,
  bands: /^\/bands(?:\/[^/]+)?$/,
} as const;

export function normalizeRoutePathname(pathname: string): string {
  const normalized = pathname.replace(/\/+$/, '');
  return normalized || Routes.root;
}

export function isKnownRoutePath(pathname: string): boolean {
  const path = normalizeRoutePathname(pathname);
  return path === Routes.root
    || path === Routes.songs
    || path === Routes.statistics
    || path === Routes.suggestions
    || path === Routes.compete
    || path === Routes.leaderboards
    || path === Routes.fullRankingsRoot
    || path === Routes.rivals
    || path === Routes.allRivalsRoot
    || path === Routes.shop
    || path === Routes.manual
    || path === Routes.settings
    || path === Routes.settingsLicenses
    || RoutePatterns.songDetail.test(path)
    || RoutePatterns.songBandLeaderboard.test(path)
    || RoutePatterns.history.test(path)
    || RoutePatterns.leaderboard.test(path)
    || RoutePatterns.player.test(path)
    || RoutePatterns.rivalDetail.test(path)
    || RoutePatterns.rivalry.test(path)
    || RoutePatterns.bandRankings.test(path)
    || RoutePatterns.playerBands.test(path)
    || RoutePatterns.bands.test(path);
}
