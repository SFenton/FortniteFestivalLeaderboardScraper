/** Centralised route path constants. */
export const Routes = {
  songs: '/songs',
  songDetail: (songId: string) => `/songs/${songId}`,
  leaderboard: (songId: string, instrument: string) => `/songs/${songId}/${instrument}`,
  playerHistory: (songId: string, instrument: string) => `/songs/${songId}/${instrument}/history`,
  player: (accountId: string) => `/player/${accountId}`,
  rivals: (accountId: string) => `/player/${accountId}/rivals`,
  rivalDetail: (accountId: string, rivalId: string) => `/player/${accountId}/rivals/${rivalId}`,
  rivalCategory: (accountId: string, rivalId: string, categoryKey: string) =>
    `/player/${accountId}/rivals/${rivalId}/${categoryKey}`,
  statistics: '/statistics',
  suggestions: '/suggestions',
  shop: '/shop',
  settings: '/settings',
} as const;

/** Regex patterns for route matching. */
export const RoutePatterns = {
  songDetail: /^\/songs\/[^/]+$/,
  leaderboard: /^\/songs\/[^/]+\/[^/]+$/,
  history: /\/history$/,
  player: /^\/player\//,
  rivals: /^\/player\/[^/]+\/rivals$/,
  rivalDetail: /^\/player\/[^/]+\/rivals\/[^/]+$/,
  rivalCategory: /^\/player\/[^/]+\/rivals\/[^/]+\/[^/]+$/,
} as const;
