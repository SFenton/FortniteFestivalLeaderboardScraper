/** Centralised route path constants. */
export const Routes = {
  songs: '/songs',
  songDetail: (songId: string) => `/songs/${songId}`,
  leaderboard: (songId: string, instrument: string) => `/songs/${songId}/${instrument}`,
  playerHistory: (songId: string, instrument: string) => `/songs/${songId}/${instrument}/history`,
  player: (accountId: string) => `/player/${accountId}`,
  rivals: '/rivals',
  allRivals: (category: string, mode?: 'leaderboard', rankBy?: string) =>
    `/rivals/all?category=${encodeURIComponent(category)}${mode ? `&mode=${encodeURIComponent(mode)}` : ''}${rankBy ? `&rankBy=${encodeURIComponent(rankBy)}` : ''}`,
  rivalDetail: (rivalId: string, rivalName?: string) =>
    `/rivals/${rivalId}${rivalName ? `?name=${encodeURIComponent(rivalName)}` : ''}`,
  rivalry: (rivalId: string, mode: string, name?: string) =>
    `/rivals/${rivalId}/rivalry?mode=${encodeURIComponent(mode)}${name ? `&name=${encodeURIComponent(name)}` : ''}`,
  statistics: '/statistics',
  suggestions: '/suggestions',
  compete: '/compete',
  leaderboards: '/leaderboards',
  fullRankings: (instrument: string, rankBy?: string, page?: number) =>
    `/leaderboards/all?instrument=${encodeURIComponent(instrument)}${rankBy ? `&rankBy=${encodeURIComponent(rankBy)}` : ''}${page != null ? `&page=${page}` : ''}`,
  fullComboRankings: (comboId: string, rankBy?: string, page?: number) =>
    `/leaderboards/all?combo=${encodeURIComponent(comboId)}${rankBy ? `&rankBy=${encodeURIComponent(rankBy)}` : ''}${page != null ? `&page=${page}` : ''}`,
  playerBands: (accountId: string, group = 'all', page?: number, name?: string) => {
    const params: string[] = [`group=${encodeURIComponent(group)}`];
    if (page != null) params.push(`page=${page}`);
    if (name) params.push(`name=${encodeURIComponent(name)}`);
    return `/bands/player/${encodeURIComponent(accountId)}?${params.join('&')}`;
  },
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
  settings: '/settings',
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
  leaderboard: /^\/songs\/[^/]+\/[^/]+$/,
  history: /\/history$/,
  player: /^\/player\//,
  rivals: /^\/rivals$/,
  allRivals: /^\/rivals\/all/,
  rivalDetail: /^\/rivals\/[^/]+$/,
  rivalry: /^\/rivals\/[^/]+\/rivalry/,
  leaderboards: /^\/leaderboards/,
  playerBands: /^\/bands\/player\//,
  bands: /^\/bands/,
} as const;
